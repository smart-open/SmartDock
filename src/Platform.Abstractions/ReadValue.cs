namespace Platform.Abstractions;

/// <summary>一次采集结果：值 + 质量 + 采集时间（三列不可拆）。</summary>
/// <param name="PointId">平台点位 ID。</param>
/// <param name="Value">解析后的值；质量不为 Good 时可能为 null。</param>
/// <param name="Quality">质量码。</param>
/// <param name="TimestampUtc">采集时间（UTC）。</param>
public readonly record struct ReadValue(
    long PointId,
    double? Value,
    QualityCode Quality,
    DateTime TimestampUtc);
