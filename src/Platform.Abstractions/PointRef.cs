namespace Platform.Abstractions;

/// <summary>点位引用：设备 + 点地址的唯一标识（7.x device_point 表在内存中的投影）。</summary>
/// <param name="PointId">平台点位 ID（数据库主键）。</param>
/// <param name="DeviceId">设备 ID。</param>
/// <param name="PointCode">业务点号，如 load_1。</param>
/// <param name="Address">协议地址表达式，如 <c>40001:Float32:CDAB</c>。</param>
/// <param name="DataType">标称数据类型。</param>
public readonly record struct PointRef(
    long PointId,
    long DeviceId,
    string PointCode,
    string Address,
    string DataType);
