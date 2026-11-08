using Platform.Core.Acquisition;
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
    private readonly ILogger<AcquisitionHostedService> _logger;

    public AcquisitionHostedService(
        PollingScheduler scheduler,
        DeviceRegistryStore registryStore,
        ILogger<AcquisitionHostedService> logger)
    {
        _scheduler = scheduler;
        _registryStore = registryStore;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var devices = await _registryStore.LoadAsync(cancellationToken);
        _logger.LogInformation("device registry loaded: {DeviceCount} devices, {PointCount} points",
            devices.Count, devices.Sum(d => d.Points.Count));

        _scheduler.SetDevices(devices);
        _scheduler.Start();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
        => await _scheduler.DisposeAsync();
}
