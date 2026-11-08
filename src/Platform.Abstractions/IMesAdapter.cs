// 02 §6.2/6.5 北向适配器契约：按 target 选插件推送；Outbox 载荷 → 对方格式在适配器内完成（6.3）。
// 只依赖 BCL（分层单向依赖 2.1：Abstractions 不准引 NuGet 包）。

namespace Platform.Abstractions;

/// <summary>Outbox 报文信封：表原始列的进程内投影（6.3 映射前形态）。</summary>
public sealed record MesEnvelope(
    long Id,
    string BizType,
    string Target,
    string IdempotencyKey,
    string PayloadJson,
    int Attempts);

/// <summary>推送结果：Success=对方明确确认；失败分「可重试」与「结果未知」（超时必须转对账，不盲目重发，6.5）。</summary>
public sealed record PushResult(bool Success, bool ResultUnknown, string? AckValue, string? Error)
{
    public static PushResult Ok(string? ackValue) => new(true, false, ackValue, null);
    public static PushResult Retryable(string error) => new(false, false, null, error);
    public static PushResult Unknown(string error) => new(false, true, null, error);
}

/// <summary>北向适配器契约：一个 target 系统一个实现（M2-3 接真实适配器，M2-1 只定契约）。</summary>
public interface IMesAdapter
{
    /// <summary>适配器唯一标识（mes_outbox.target 引用；工厂按此路由）。</summary>
    string Target { get; }

    /// <summary>推送一条报文。幂等由 idempotencyKey 透传给对方；对方不认幂等时适配器自行先查再推（6.4 铁律 3）。</summary>
    Task<PushResult> PushAsync(MesEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>Outbox 存储契约（6.5）：Core 的 OutboxWorker 只依赖此接口（Data 实现走 PG）。</summary>
public interface IMesOutboxStore
{
    Task<IReadOnlyList<MesEnvelope>> ClaimBatchAsync(int batchSize, CancellationToken cancellationToken = default);

    Task MarkAckedAsync(long id, string? ackValue, CancellationToken cancellationToken = default);

    Task BackoffAsync(long id, int attempts, string error, CancellationToken cancellationToken = default);

    Task SuspectAsync(long id, string reason, CancellationToken cancellationToken = default);
}
