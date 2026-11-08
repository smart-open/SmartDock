namespace Platform.Abstractions.Remote;

/// <summary>
/// 进程外驱动契约（4.7 L2：由 PluginHost 子进程暴露，平台侧只依赖这个接口）。
/// M1-8 最小骨架：读 + 连接 + 心跳；写与事件订阅随真实原生驱动插件补。
/// </summary>
public interface IRemoteDriver
{
    /// <summary>endpoint 原样 JSON 透传（各协议自解释，与 DeviceConfig.endpoint 列一致）。</summary>
    Task<ConnectResult> ConnectAsync(string endpointJson, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReadValue>> ReadAsync(IReadOnlyList<PointRef> points, CancellationToken cancellationToken = default);
}

/// <summary>驱动插件清单（5.11.4 ②：arch 声明决定隔离方式；位数不匹配必须 Blocked）。</summary>
public sealed record DriverManifest(
    string DriverId,
    string AssemblyPath,
    /// <summary>x86 = 必须进 32 位子进程；any = 可随主进程。</summary>
    string Arch);

/// <summary>宿主→子进程心跳应答（子进程存活 + 当前驱动状态摘要）。</summary>
public sealed record HostPingResult(bool Alive, string? LastError);
