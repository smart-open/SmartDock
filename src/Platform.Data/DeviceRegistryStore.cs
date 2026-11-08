using Npgsql;
using Platform.Abstractions;

namespace Platform.Data;

/// <summary>
/// 设备台账加载器（7.x device / device_point 只读投影；M1 南向地基）。
/// 一次性全量加载（台账量级 ≤ 数千行），启动时读一次；热更新属后续能力。
/// 只用 ADO.NET——台账与快照同属高频路径，禁走 EF（2.4 决策的范围延伸）。
/// </summary>
public sealed class DeviceRegistryStore
{
    private readonly NpgsqlDataSource _dataSource;

    public DeviceRegistryStore(string connectionString)
    {
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
    }

    /// <summary>加载全部启用设备及其点位。设备无点位、点位地址非法时跳过并告警，不阻断启动（3.2 失败策略）。</summary>
    public async Task<IReadOnlyList<DeviceEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string deviceSql = """
            SELECT id, device_code, driver_id, endpoint::text, poll_ms
            FROM device WHERE enabled
            ORDER BY id
            """;
        var devices = new List<DeviceConfig>();
        await using (var command = new NpgsqlCommand(deviceSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                devices.Add(new DeviceConfig(
                    DeviceId: reader.GetInt64(0),
                    DeviceCode: reader.GetString(1),
                    DriverId: reader.GetString(2),
                    Endpoint: reader.GetString(3),
                    PollMs: reader.GetInt32(4)));
            }
        }

        const string pointSql = """
            SELECT dp.id, dp.device_id, dp.point_code, dp.address, dp.data_type
            FROM device_point dp
            JOIN device d ON d.id = dp.device_id
            WHERE d.enabled
            ORDER BY dp.id
            """;
        var pointsByDevice = new Dictionary<long, List<PointRef>>();
        await using (var command = new NpgsqlCommand(pointSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var deviceId = reader.GetInt64(1);
                if (!pointsByDevice.TryGetValue(deviceId, out var points))
                {
                    points = [];
                    pointsByDevice[deviceId] = points;
                }

                points.Add(new PointRef(
                    PointId: reader.GetInt64(0),
                    DeviceId: deviceId,
                    PointCode: reader.GetString(2),
                    Address: reader.GetString(3),
                    DataType: reader.GetString(4)));
            }
        }

        return devices
            .Select(device => new DeviceEntry(
                device,
                pointsByDevice.TryGetValue(device.DeviceId, out var points) ? points : []))
            .ToArray();
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}

/// <summary>设备 + 其点位集合。</summary>
public sealed record DeviceEntry(DeviceConfig Device, IReadOnlyList<PointRef> Points);
