namespace Platform.Abstractions.Remote;

/// <summary>
/// 进程外驱动契约（4.7 L2：由 PluginHost 子进程暴露，平台侧只依赖这个接口）。
/// 连接 + 批量读 + 批量写 + 事件订阅，与 4.7 的 IRemoteDriver 设计签名一一对应。
/// </summary>
public interface IRemoteDriver
{
    /// <summary>endpoint 原样 JSON 透传（各协议自解释，与 DeviceConfig.endpoint 列一致）。</summary>
    Task<ConnectResult> ConnectAsync(string endpointJson, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReadValue>> ReadAsync(IReadOnlyList<PointRef> points, CancellationToken cancellationToken = default);
    /// <summary>批量写（5.6 写路径的远程形态；每点独立结果，失败不抛）。</summary>
    Task<IReadOnlyList<WriteResult>> WriteAsync(
        IReadOnlyList<(PointRef Point, double Value)> writes, CancellationToken cancellationToken = default);
    /// <summary>驱动侧事件流（连接变化 / 写完成等）；消费方停止枚举即退订。</summary>
    IAsyncEnumerable<DeviceEvent> SubscribeAsync(CancellationToken cancellationToken = default);
}

/// <summary>驱动侧事件（Subscribe 契约载体）；payload 为各事件类型的自解释 JSON。</summary>
public sealed record DeviceEvent(
    string EventType,
    long DeviceId,
    string PayloadJson,
    DateTime TimestampUtc);

/// <summary>
/// 驱动事件源（可选契约）：产生连接变化/写完成等事件的驱动实现此接口，
/// 宿主据此转发 Subscribe 流；只读且无事件的驱动不实现（接口隔离）。
/// </summary>
public interface IDeviceEventSource
{
    /// <summary>事件流；消费方停止枚举即退订，取消/宿主退出视为流正常结束。</summary>
    IAsyncEnumerable<DeviceEvent> SubscribeAsync(CancellationToken cancellationToken = default);
}

/// <summary>驱动插件清单（5.11.4 ②：arch 声明决定隔离方式；位数不匹配必须 Blocked）。</summary>
public sealed record DriverManifest(
    string DriverId,
    string AssemblyPath,
    /// <summary>x86 = 必须进 32 位子进程；any = 可随主进程。</summary>
    string Arch)
{
    /// <summary>
    /// 5.11.4 ② arch 判定的唯一来源：x86 manifest 必须进 32 位子进程（雷赛/正运动/VisionPro
    /// 都是 x86 原生 SDK，装进 x64 主进程直接崩）。宿主位数选择（RemoteDriverSupervisor）与本
    /// 判定共用此属性，规则改动只动这里。未知值按 x64 处理——manifest 出错宁可装错子进程
    /// 也不能把 x86 卡拉进 x64 主进程。
    /// </summary>
    public bool Requires32BitProcess => string.Equals(Arch, "x86", StringComparison.OrdinalIgnoreCase);
}

/// <summary>宿主→子进程心跳应答（子进程存活 + 当前驱动状态摘要）。</summary>
public sealed record HostPingResult(bool Alive, string? LastError);
