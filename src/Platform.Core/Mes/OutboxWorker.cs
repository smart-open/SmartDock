// 02 §6.5 Outbox 实现：主循环按 6.5 节选逐步落地——认领（SKIP LOCKED）→ 按 target 路由适配器 →
// 成功 Ack / 可重试退避 / 超时转对账（不盲目重发）/ 异常退避。空批 1s 轮询。

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Platform.Abstractions;

namespace Platform.Core.Mes;

/// <summary>适配器路由：按 target 选插件（6.5 adapters.Get(msg.Target)）。</summary>
public interface IMesAdapterProvider
{
    /// <summary>找不到 target 的适配器时抛异常（错误配置应显式失败而不是静默丢报文）。</summary>
    IMesAdapter Get(string target);
}

public sealed class OutboxWorker(
    IMesOutboxStore store,
    IMesAdapterProvider adapters,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("outbox worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var batch = await store.ClaimBatchAsync(BatchSize, stoppingToken);
            if (batch.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            foreach (var message in batch)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                IMesAdapter adapter;
                try
                {
                    adapter = adapters.Get(message.Target);
                }
                catch (Exception ex)
                {
                    // 适配器缺失属配置错误：可重试失败处理（等配置修复后退避重试），不丢报文
                    logger.LogError(ex, "adapter not found for target {Target}, msg {MsgId}", message.Target, message.Id);
                    await store.BackoffAsync(message.Id, message.Attempts, ex.Message, stoppingToken);
                    continue;
                }

                try
                {
                    var result = await adapter.PushAsync(message, stoppingToken);
                    if (result.Success)
                    {
                        await store.MarkAckedAsync(message.Id, result.AckValue, stoppingToken);
                        logger.LogInformation("outbox {MsgId} acked (target={Target})", message.Id, message.Target);
                    }
                    else if (result.ResultUnknown)
                    {
                        // 超时/结果未知：转对账，绝不自动重发（重复入账比晚入账严重，6.5 catch 链）
                        await store.SuspectAsync(message.Id, result.Error ?? "unknown", stoppingToken);
                        logger.LogWarning("outbox {MsgId} suspended (result unknown): {Error}", message.Id, result.Error);
                    }
                    else
                    {
                        await store.BackoffAsync(message.Id, message.Attempts, result.Error ?? "failed", stoppingToken);
                        logger.LogWarning("outbox {MsgId} retry scheduled (attempt {Attempts}): {Error}",
                            message.Id, message.Attempts, result.Error);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // 适配器内部超时（HttpClient TaskCanceledException）：结果未知 → 对账
                    await store.SuspectAsync(message.Id, "timeout", stoppingToken);
                }
                catch (TimeoutException ex)
                {
                    await store.SuspectAsync(message.Id, ex.Message, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MES push exception {MsgId}", message.Id);
                    await store.BackoffAsync(message.Id, message.Attempts, ex.Message, stoppingToken);
                }
            }
        }

        logger.LogInformation("outbox worker stopped");
    }
}
