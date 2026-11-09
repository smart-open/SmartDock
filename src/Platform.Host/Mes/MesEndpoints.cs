// 02 §6.2/6.3 M2-1 试用端点：报工入 Outbox（先落库再动作 0.4 原则 3 的北向版）。
// 幂等键平台组装（6.4）；重复键由唯一索引挡住并返回 duplicate=true（不重复入账）。

using System.Text.Json;
using Platform.Core.Mes;
using Platform.Data;

namespace Platform.Host.Mes;

public static class MesEndpoints
{
    public static void MapMesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/mes");

        // 报工上传（6.2 接口 3）：workOrderNo + stepNo 必填，payload 原文透传入 Outbox
        group.MapPost("/report", async (ReportRequest request, MesOutboxStore store) =>
        {
            var idempotencyKey = MesIdempotencyKeyBuilder.ProductionReport(request.WorkOrderNo, request.StepNo);
            var payloadJson = JsonSerializer.Serialize(request);
            var inserted = await store.TryEnqueueAsync("production.report", request.Target ?? "mes.mock",
                idempotencyKey, payloadJson);
            return Results.Ok(new { idempotencyKey, inserted, duplicate = !inserted });
        });

        // 设备状态（6.2 接口 7）：deviceCode + state + 5min 时间窗
        group.MapPost("/state", async (StateRequest request, MesOutboxStore store) =>
        {
            var idempotencyKey = MesIdempotencyKeyBuilder.EquipmentState(
                request.DeviceCode, request.State, DateTimeOffset.UtcNow);
            var payloadJson = JsonSerializer.Serialize(request);
            var inserted = await store.TryEnqueueAsync("equipment.state", request.Target ?? "mes.mock",
                idempotencyKey, payloadJson);
            return Results.Ok(new { idempotencyKey, inserted, duplicate = !inserted });
        });

        // 每日对账（M2-3，6.6 对账任务 / 04 验收 #22）：按日触发（默认昨天），结果 upsert 落 mes_reconcile
        group.MapPost("/reconcile", async (ReconcileRequest request, MesReconcileStore store) =>
        {
            var bizDate = request.Date ?? DateOnly.FromDateTime(DateTime.Now.AddDays(-1));
            var results = await store.ReconcileAsync(bizDate);
            return Results.Ok(results);
        });

        // 对账结果查询（报表数据源）
        group.MapGet("/reconcile/{date}", async (DateOnly date, MesOutboxStore outbox, Npgsql.NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new Npgsql.NpgsqlCommand("""
                SELECT biz_type, total_sent, total_acked, total_suspended, total_dead, total_pending,
                       diff_count, diff_ratio, alert
                FROM mes_reconcile WHERE biz_date = @date ORDER BY biz_type
                """, connection);
            command.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));

            var rows = new List<object>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new
                {
                    bizType = reader.GetString(0),
                    totalSent = reader.GetInt32(1),
                    totalAcked = reader.GetInt32(2),
                    totalSuspended = reader.GetInt32(3),
                    totalDead = reader.GetInt32(4),
                    totalPending = reader.GetInt32(5),
                    diffCount = reader.GetInt32(6),
                    diffRatio = reader.GetDecimal(7),
                    alert = reader.GetBoolean(8),
                });
            }

            return Results.Ok(rows);
        });

        // 死信人工重放（04 验收 #21：Dead → Pending，attempts 清零重走阶梯）
        group.MapPost("/dead/{id}/replay", async (long id, MesOutboxStore store) =>
        {
            await store.RequeueAsync(id);
            return Results.Ok(new { id, requeued = true });
        });
    }
}

public sealed record ReportRequest(string WorkOrderNo, int StepNo, decimal GoodQty, decimal ScrapQty, string? Target = null);
public sealed record StateRequest(string DeviceCode, string State, string? Target = null);
public sealed record ReconcileRequest(DateOnly? Date);
