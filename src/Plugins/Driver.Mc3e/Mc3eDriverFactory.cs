using System.Text.Json;
using Platform.Abstractions;

namespace Driver.Mc3e;

/// <summary>
/// MC 3E 驱动工厂（4.8.1：插件自带装配逻辑）。
/// endpoint JSON 格式：{"host":"127.0.0.1","port":6000}
/// —— 协议连接参数只有驱动自己懂，平台不做解释（契约先行）。
/// </summary>
public sealed class Mc3eDriverFactory : IDeviceDriverFactory
{
    public string DriverId => Mc3eDriver.DriverId;

    public IDeviceDriver Create(DeviceConfig device)
    {
        using var document = JsonDocument.Parse(device.Endpoint);
        var root = document.RootElement;

        // host 缺失属于台账配置错误：直接抛，让调度器退避重试并告警（3.2）
        var host = root.GetProperty("host").GetString()
            ?? throw new NotSupportedException($"device {device.DeviceCode}: endpoint.host missing");
        var port = root.TryGetProperty("port", out var portElement) ? portElement.GetInt32() : 6000;

        return new Mc3eDriver(host, port);
    }
}
