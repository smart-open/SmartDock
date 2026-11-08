namespace Platform.Abstractions;

/// <summary>驱动插件自描述（5.2 驱动契约；M0 只含最小字段）。</summary>
/// <param name="DriverId">全局唯一驱动标识，如 driver.modbus.tcp。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="SupportsWrite">是否支持写（M0 只读）。</param>
/// <param name="MaxBatchPerRequest">单次请求最大批量点数。</param>
public sealed record DriverDescriptor(
    string DriverId,
    string DisplayName,
    bool SupportsWrite,
    int MaxBatchPerRequest);
