// 02 §6.6 兜底与对账调度：
// ① 每 10min 扫一次 Sending 卡死（Worker 崩溃兜底，6.6 引文「没有这一步，消息会永久卡住」）；
// ② 每日 02:00 对账（6.6 对账任务），结果落 mes_reconcile，告警条件在 store 内判定。

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Platform.Data;

namespace Platform.Core.Mes;

public sealed class ReconcileScheduler(
    MesReconcileStore store,
    ILogger<ReconcileScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan StuckScanInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReconcileCheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DailyRunHour = new(2, 0, 0);

    private DateOnly _lastReconciledDate = DateOnly.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("reconcile scheduler started");
        var lastStuckScan = DateTime.MinValue;
        var lastReconcileCheck = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;

            // ① Sending 卡死扫描（每 10min）
            if (now - lastStuckScan >= StuckScanInterval)
            {
                lastStuckScan = now;
                try
                {
                    var requeued = await store.RequeueStuckSendingAsync(stoppingToken);
                    if (requeued > 0)
                    {
                        // 兜底触发即说明有 Worker 异常中断，必须留痕
                        logger.LogWarning("requeue stuck sending messages: {Count}", requeued);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "stuck sending scan failed");
                }
            }

            // ② 每日对账（02:00 后首个检查点跑一次；服务重启不重跑已对过的日子）
            if (now - lastReconcileCheck >= ReconcileCheckInterval)
            {
                lastReconcileCheck = now;
                var today = DateOnly.FromDateTime(now);
                // 对「昨天」跑：昨天整天的报文才可能到达终态（今天的还在发送窗口内）
                var reconcileDate = today.AddDays(-1);
                if (now.TimeOfDay >= DailyRunHour && _lastReconciledDate < reconcileDate)
                {
                    try
                    {
                        var results = await store.ReconcileAsync(reconcileDate, stoppingToken);
                        _lastReconciledDate = reconcileDate;
                        foreach (var result in results)
                        {
                            if (result.Alert)
                            {
                                // 告警即日志（告警通道随 M3 通知体系接入；差异明细在 mes_reconcile.detail 可定位）
                                logger.LogWarning(
                                    "MES reconcile ALERT {Date} {BizType}: sent={Sent} acked={Acked} suspended={Susp} dead={Dead} diff={Diff} ({Ratio:P})",
                                    result.BizDate, result.BizType, result.TotalSent, result.TotalAcked,
                                    result.TotalSuspended, result.TotalDead, result.DiffCount, result.DiffRatio);
                            }
                            else
                            {
                                logger.LogInformation(
                                    "MES reconcile {Date} {BizType}: sent={Sent} acked={Acked} diff={Diff}",
                                    result.BizDate, result.BizType, result.TotalSent, result.TotalAcked, result.DiffCount);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "daily reconcile failed for {Date}", reconcileDate);
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }

        logger.LogInformation("reconcile scheduler stopped");
    }
}
