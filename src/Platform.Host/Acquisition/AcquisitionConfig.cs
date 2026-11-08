namespace Platform.Host.Acquisition;

/// <summary>M0 采集配置（appsettings "Acquisition" 节，M1 迁到设备台账）。</summary>
public sealed class AcquisitionConfig
{
    public const string SectionName = "Acquisition";

    public string ModbusHost { get; init; } = "127.0.0.1";
    public int ModbusPort { get; init; } = 5020;
    public byte ModbusUnitId { get; init; } = 1;
    public int PointCount { get; init; } = 10;
    public int PeriodSeconds { get; init; } = 1;
}
