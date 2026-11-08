using Platform.Data;

namespace Platform.Host.Acquisition;

/// <summary>分区运维配置（appsettings Partition 节）。</summary>
public sealed class PartitionMaintenanceConfig
{
    public const string SectionName = "Partition";

    /// <summary>热数据保留天数（7.4 默认 90）。</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>运维周期小时数（默认 24h 一轮；启动时先跑一次兜底）。</summary>
    public int IntervalHours { get; set; } = 24;
}

/// <summary>
/// 分区运维后台服务（7.4：建下月分区提前做；过期分区按保留期删除）。
/// 启动即跑一轮（兜底长期停机后分区缺失），此后按周期执行。
/// </summary>
public sealed class PartitionMaintenanceService : BackgroundService
{
    private readonly PartitionMaintainer _maintainer;
    private readonly PartitionMaintenanceConfig _config;
    private readonly ILogger<PartitionMaintenanceService> _logger;

    public PartitionMaintenanceService(
        PartitionMaintainer maintainer,
        Microsoft.Extensions.Options.IOptions<PartitionMaintenanceConfig> config,
        ILogger<PartitionMaintenanceService> logger)
    {
        _maintainer = maintainer;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("partition maintenance started: retention={RetentionDays}d, interval={IntervalHours}h",
            _config.RetentionDays, _config.IntervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (created, dropped) = await _maintainer.RunAsync(_config.RetentionDays, stoppingToken);
                _logger.LogInformation("partition maintenance done: created={Created}, dropped={Dropped}", created, dropped);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // DB 暂不可达等故障不能杀死后台服务：记日志等下轮（与采集循环同一失败策略）
                _logger.LogError(ex, "partition maintenance failed, retry next cycle");
            }

            await Task.Delay(TimeSpan.FromHours(Math.Max(_config.IntervalHours, 1)), stoppingToken);
        }
    }
}
