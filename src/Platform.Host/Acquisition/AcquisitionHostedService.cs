using Platform.Core.Acquisition;
using Platform.Core.Alarms;
using Platform.Data;

namespace Platform.Host.Acquisition;

/// <summary>
/// 把采集循环挂进 Host 生命周期（启停跟随进程）。
/// M1：启动时从设备台账加载设备清单（3.2 失败策略：台账不可用 → 阻止启动采集）。
/// </summary>
public sealed class AcquisitionHostedService : IHostedService
{
    private readonly PollingScheduler _scheduler;
    private readonly DeviceRegistryStore _registryStore;
    private readonly AlarmStore _alarmStore;
    private readonly ILogger<AcquisitionHostedService> _logger;

    public AcquisitionHostedService(
        PollingScheduler scheduler,
        DeviceRegistryStore registryStore,
        AlarmStore alarmStore,
        ILogger<AcquisitionHostedService> logger)
    {
        _scheduler = scheduler;
        _registryStore = registryStore;
        _alarmStore = alarmStore;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var devices = await _registryStore.LoadAsync(cancellationToken);
        _logger.LogInformation("device registry loaded: {DeviceCount} devices, {PointCount} points",
            devices.Count, devices.Sum(d => d.Points.Count));

        _scheduler.SetDevices(devices);
        // C1 最小报警源：设备连接态变化 → alarm 表（离线 raise / 恢复 clear）。
        // 回调在采集循环线程触发，落库失败只记日志——报警缺失不该打断采集。
        _scheduler.DeviceConnectionChanged += (deviceId, deviceCode, connected) =>
        {
            var alarmNo = $"device:{deviceId}:offline";
            _ = Task.Run(async () =>
            {
                try
                {
                    if (connected)
                    {
                        await _alarmStore.ClearAsync(alarmNo);
                        _logger.LogInformation("alarm cleared: {AlarmNo}", alarmNo);
                    }
                    else
                    {
                        await _alarmStore.RaiseAsync(alarmNo, deviceId, level: 3,
                            code: "DEVICE_OFFLINE", message: $"设备 {deviceCode} 连接中断");
                        _logger.LogWarning("alarm raised: {AlarmNo}", alarmNo);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "alarm persist failed for {AlarmNo}", alarmNo);
                }
            });
        };
        _scheduler.Start();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
        => await _scheduler.DisposeAsync();
}
