// M2-1 Mock 适配器：进程内"虚拟 MES"，M2-4 断网演练（12.9.2 第 7 项）用它注入三种行为——
// 正常确认 / 可重试失败 / 结果未知（超时）。配置走 mes.mock 配置节，运行期不重载（M1 同口径）。

using System.Text.Json;
using Microsoft.Extensions.Options;
using Platform.Abstractions;

namespace Platform.Host.Mes;

public sealed class MockMesOptions
{
    public const string SectionName = "MesMock";

    /// <summary>故障注入模式：ok=全部确认；reject=全部可重试失败；unknown=全部结果未知（超时口径）。</summary>
    public string Mode { get; init; } = "ok";

    /// <summary>模拟推送耗时 ms（验证 Worker 批处理非阻塞）。</summary>
    public int LatencyMs { get; init; } = 0;
}

public sealed class MockMesAdapter : IMesAdapter
{
    // 已处理幂等键记录（6.4 铁律 2：同一键重复推送返回"已处理"）——Mock 内存版，重启即清（真实适配器查对方系统）
    private readonly HashSet<string> _processedKeys = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public string Target => "mes.mock";

    public async Task<PushResult> PushAsync(MesEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        if (options.LatencyMs > 0)
        {
            await Task.Delay(options.LatencyMs, cancellationToken);
        }

        lock (_lock)
        {
            // 同键重推：Mock 返回"已处理"确认（真实 MES 语义），Ack 不再入账
            if (!_processedKeys.Add(envelope.IdempotencyKey))
            {
                return PushResult.Ok($"duplicate:{envelope.IdempotencyKey}");
            }
        }

        return options.Mode switch
        {
            "ok" => PushResult.Ok($"ack:{envelope.Id}"),
            "reject" => PushResult.Retryable("mock: reject mode"),
            "unknown" => PushResult.Unknown("mock: unknown mode"),
            _ => PushResult.Retryable($"mock: bad mode {options.Mode}"),
        };
    }

    private readonly IOptionsMonitor<MockMesOptions> _options;

    public MockMesAdapter(IOptionsMonitor<MockMesOptions> options)
    {
        _options = options;
    }
}
