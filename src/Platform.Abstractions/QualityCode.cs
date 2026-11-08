namespace Platform.Abstractions;

/// <summary>点位值质量码（5.7：值 + 质量 + 时间三列，缺一不可）。</summary>
public enum QualityCode
{
    /// <summary>值有效。</summary>
    Good = 0,

    /// <summary>值不确定（如超时重试后、设备时间戳过期）。</summary>
    Uncertain = 1,

    /// <summary>值无效（通信失败、地址错误等）。</summary>
    Bad = 2,

    /// <summary>从未采集过（初始快照占位）。</summary>
    Stale = 3,
}
