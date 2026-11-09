// 02 §C3 报警确认端点 + §C1 报警查询（M3-1）。
// 确认权限复用 M1-9 的 X-Operator 口径（与写指令同权的敏感操作）；确认留 acked_by 审计线索。

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Platform.Core.Alarms;

namespace Platform.Host.Alarms;

public static class AlarmEndpoints
{
    public static void MapAlarmEndpoints(this WebApplication app)
    {
        app.MapGet("/api/alarms", (AlarmStore store, bool? active) =>
            Results.Ok(store.ListAsync(active ?? true).GetAwaiter().GetResult()));

        app.MapPost("/api/alarms/{id:long}/ack", (long id, AlarmStore store, HttpRequest request) =>
        {
            var operatorHeader = request.Headers["X-Operator"].FirstOrDefault();
            if (!long.TryParse(operatorHeader, out var operatorId))
            {
                return Results.Unauthorized();
            }

            var acked = store.AckAsync(id, operatorId).GetAwaiter().GetResult();
            return acked ? Results.Ok(new { acked = true }) : Results.NotFound(new { acked = false });
        });
    }
}
