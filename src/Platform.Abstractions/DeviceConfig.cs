namespace Platform.Abstractions;

/// <summary>
/// 设备台账在内存中的投影（7.x device 表；M1 南向：采集配置从 appsettings 迁到数据库）。
/// </summary>
/// <param name="DeviceId">数据库主键。</param>
/// <param name="DeviceCode">业务设备号，如 SIM-001。</param>
/// <param name="DriverId">驱动标识，如 driver.modbus.tcp。</param>
/// <param name="Endpoint">连接参数 JSON（host/port/unitId 等，由驱动工厂解释）。</param>
/// <param name="PollMs">采集周期毫秒。</param>
public sealed record DeviceConfig(
    long DeviceId,
    string DeviceCode,
    string DriverId,
    string Endpoint,
    int PollMs);
