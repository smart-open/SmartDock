namespace Platform.Abstractions;

/// <summary>
/// 可写驱动契约（5.6 WriteGuard 的驱动侧；5.2 驱动契约的写扩展）。
/// 只有需要写能力的驱动实现此接口——只读驱动不受影响（接口隔离）。
/// 写失败不抛异常，返回结果对象（与 ConnectAsync 同语义，5.12.3）。
/// </summary>
public interface IDeviceWriteDriver : IDeviceDriver
{
    /// <summary>
    /// 写单个值（驱动负责按点位地址/类型编码，如 Float32 → 2 个保持寄存器 FC16）。
    /// </summary>
    Task<WriteResult> WriteAsync(PointRef point, double value, CancellationToken cancellationToken = default);
}

/// <summary>写结果：成功/失败原因（异常不外抛）。</summary>
public readonly record struct WriteResult(bool Success, string? Error);
