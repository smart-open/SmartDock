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
        // 原实现 GetAwaiter().GetResult() 阻塞线程池（高并发轮询白耗线程），改 async
        app.MapGet("/api/alarms", async (AlarmStore store, bool? active) =>
            Results.Ok(await store.ListAsync(active ?? true)));

        app.MapPost("/api/alarms/{id:long}/ack", async (long id, AlarmStore store, HttpRequest request) =>
        {
            var operatorHeader = request.Headers["X-Operator"].FirstOrDefault();
            if (!long.TryParse(operatorHeader, out var operatorId))
            {
                return Results.Unauthorized();
            }

            var acked = await store.AckAsync(id, operatorId);
            return acked ? Results.Ok(new { acked = true }) : Results.NotFound(new { acked = false });
        });
    }
}
