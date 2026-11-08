using Platform.Core.Acquisition;

namespace Platform.Host.Acquisition;

/// <summary>把采集循环挂进 Host 生命周期（启停跟随进程）。</summary>
public sealed class AcquisitionHostedService : IHostedService
{
    private readonly PollingScheduler _scheduler;

    public AcquisitionHostedService(PollingScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _scheduler.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
        => await _scheduler.DisposeAsync();
}
