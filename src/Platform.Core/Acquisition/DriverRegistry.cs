using Platform.Abstractions;

namespace Platform.Core.Acquisition;

/// <summary>
/// 驱动注册表（4.8.1）：按 driver_id 路由到插件自带工厂。
/// 核心只认契约（IDeviceDriverFactory），不引用任何具体驱动——
/// 新协议 = 新插件 + 注册一行，不改内核（11.6 扩展点）。
/// </summary>
public sealed class DriverRegistry
{
    private readonly Dictionary<string, IDeviceDriverFactory> _factories;

    public DriverRegistry(IEnumerable<IDeviceDriverFactory> factories)
    {
        _factories = factories.ToDictionary(f => f.DriverId, StringComparer.Ordinal);
    }

    /// <summary>创建设备驱动实例。未知 driver_id / 解析失败抛异常，由调度器退避重试（3.2）。</summary>
    public IDeviceDriver CreateDriver(DeviceConfig device)
    {
        if (!_factories.TryGetValue(device.DriverId, out var factory))
        {
            throw new NotSupportedException(
                $"unknown driver_id: {device.DriverId} (device {device.DeviceCode}); registered: [{string.Join(", ", _factories.Keys)}]");
        }

        return factory.Create(device);
    }
}
