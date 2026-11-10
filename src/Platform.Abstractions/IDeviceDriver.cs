namespace Platform.Abstractions;

/// <summary>
/// 驱动契约（5.2）：驱动只负责「连上、读回、按契约返回」，连接管理/重试/落库归平台。
/// 实现必须是线程安全的批量异步接口；单点失败只标该点 Bad，不抛出。
/// </summary>
public interface IDeviceDriver : IAsyncDisposable
{
    /// <summary>驱动自描述。</summary>
    DriverDescriptor Describe();

    /// <summary>
    /// 当前传输层连接状态（观测面）：true = TCP 会话健在。
    /// 调度器用它区分「通信故障」（连接已断 → 报离线 + 重连）与「配置错误」
    /// （连接健在但地址解析失败全 Bad → 只报点位 Bad，不做离线翻转，避免报警振荡）。
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 建立连接。幂等：已连接直接返回成功；失败不抛异常，返回结果对象。
    /// </summary>
    Task<ConnectResult> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>断开连接。幂等：重复调用安全。</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量读。地址解析、字节序、类型换算由驱动完成；返回顺序与请求一致。
    /// </summary>
    Task<IReadOnlyList<ReadValue>> ReadAsync(
        IReadOnlyList<PointRef> points,
        CancellationToken cancellationToken = default);
}

/// <summary>连接结果：成功/失败原因（5.12.3：异常不外抛）。</summary>
public readonly record struct ConnectResult(bool Success, string? Error);
