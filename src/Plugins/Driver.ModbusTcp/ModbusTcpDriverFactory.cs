using System.Text.Json;
using Platform.Abstractions;

namespace Driver.ModbusTcp;

/// <summary>
/// Modbus TCP 驱动工厂（4.8.1：插件自带装配逻辑）。
/// endpoint JSON 格式：{"host":"127.0.0.1","port":5020,"unitId":1}
/// —— 协议连接参数只有驱动自己懂，平台不做解释（契约先行）。
/// </summary>
public sealed class ModbusTcpDriverFactory : IDeviceDriverFactory
{
    public string DriverId => ModbusTcpDriver.DriverId;

    public IDeviceDriver Create(DeviceConfig device)
    {
        using var document = JsonDocument.Parse(device.Endpoint);
        var root = document.RootElement;

        // host/port 缺失属于台账配置错误：直接抛，让调度器退避重试并告警（3.2）
        var host = root.GetProperty("host").GetString()
            ?? throw new NotSupportedException($"device {device.DeviceCode}: endpoint.host missing");
        var port = root.GetProperty("port").GetInt32();
        var unitId = root.TryGetProperty("unitId", out var unitIdElement) ? unitIdElement.GetByte() : (byte)1;

        return new ModbusTcpDriver(host, port, unitId);
    }
}
