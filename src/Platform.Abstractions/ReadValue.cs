namespace Platform.Abstractions;

/// <summary>一次采集结果：设备 + 值 + 质量 + 采集时间（落库四要素不可拆）。</summary>
/// <param name="PointId">平台点位 ID。</param>
/// <param name="DeviceId">所属设备 ID（point_history 落库需要）。</param>
/// <param name="Value">解析后的值；质量不为 Good 时可能为 null。</param>
/// <param name="Quality">质量码。</param>
/// <param name="TimestampUtc">采集时间（UTC）。</param>
public readonly record struct ReadValue(
    long PointId,
    long DeviceId,
    double? Value,
    QualityCode Quality,
    DateTime TimestampUtc);
