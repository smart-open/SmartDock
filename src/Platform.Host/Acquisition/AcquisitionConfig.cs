namespace Platform.Host.Acquisition;

/// <summary>M0 采集配置（appsettings "Acquisition" 节）。M1 起设备参数迁到设备台账，仅保留攒批参数。</summary>
public sealed class AcquisitionConfig
{
    public const string SectionName = "Acquisition";

    /// <summary>攒批队列容量（2.6A.3：容量 = 峰值速率 × 可容忍时延）。</summary>
    public int QueueCapacity { get; init; } = 2048;

    /// <summary>攒批时间窗毫秒。</summary>
    public int FlushIntervalMs { get; init; } = 200;
}
