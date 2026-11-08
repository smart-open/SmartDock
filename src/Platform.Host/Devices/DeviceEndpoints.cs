// M1-9 设备页数据源：台账只读列表（客户端不直连数据库，2.3）。
// 台账增删改 UI 随 M3 管理页（device:manage 权限码已预埋）；M1 先满足「设备」页展示。

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Platform.Host.Devices;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/devices", async (NpgsqlDataSource dataSource) =>
        {
            await using var command = dataSource.CreateCommand(
                """
                SELECT d.id, d.device_code, d.name, d.driver_id, d.poll_ms, d.enabled,
                       COUNT(dp.id) AS point_count
                FROM device d
                LEFT JOIN device_point dp ON dp.device_id = d.id
                GROUP BY d.id
                ORDER BY d.device_code
                """);
            await using var reader = await command.ExecuteReaderAsync();

            var devices = new List<DeviceSummary>();
            while (await reader.ReadAsync())
            {
                devices.Add(new DeviceSummary(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetBoolean(5),
                    reader.GetInt64(6)));
            }

            return Results.Ok(devices);
        });

        return endpoints;
    }
}

public sealed record DeviceSummary(
    long Id,
    string DeviceCode,
    string Name,
    string DriverId,
    int PollMs,
    bool Enabled,
    long PointCount);
