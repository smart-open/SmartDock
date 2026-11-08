namespace Platform.Abstractions;

/// <summary>
/// 驱动工厂契约（4.8.1 插件装配）：
/// 每个驱动插件自带工厂，负责解释自己的 endpoint JSON——平台不理解协议细节。
/// 平台按 device.driver_id 从注册表找到工厂；未知 driver_id 必须抛
/// NotSupportedException（调度器会捕获并退避重试，见 3.2 失败策略）。
/// </summary>
public interface IDeviceDriverFactory
{
    /// <summary>该工厂能创建的驱动标识（与 device.driver_id 匹配，如 driver.modbus.tcp）。</summary>
    string DriverId { get; }

    /// <summary>按台账配置创建驱动实例。endpoint JSON 由驱动插件自行解释。</summary>
    IDeviceDriver Create(DeviceConfig device);
}
