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

        // 报工上传（6.2 接口 3）：workOrderNo + stepNo 必填，payload 原文透传入 Outbox。
        // seq 不能用 MesIdempotencyKeyBuilder 的进程内存计数器——重启后从 1 重计，
        // 会与历史键撞车被唯一索引误判重（新报工静默丢失）；改为 DB 前缀计数（与
        // WorkOrderStore.ChangeStatusAsync 同口径），order_no 含 %/_ 时须转义。
        group.MapPost("/report", async (ReportRequest request, Npgsql.NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await using var seqCommand = new Npgsql.NpgsqlCommand("""
                    SELECT COUNT(*) FROM mes_outbox
                    WHERE biz_type = 'production.report' AND idempotency_key LIKE @like ESCAPE '\'
                    """, connection, transaction);
                var prefix = $"production.report:{EscapeLike(request.WorkOrderNo)}:{request.StepNo}";
                seqCommand.Parameters.AddWithValue("like", prefix + ":%");
                var seq = (int)(long)(await seqCommand.ExecuteScalarAsync())! + 1;
                var idempotencyKey = $"{prefix}:{seq}";

                var payloadJson = JsonSerializer.Serialize(request);
                await using var insertCommand = new Npgsql.NpgsqlCommand("""
                    INSERT INTO mes_outbox (biz_type, target, idempotency_key, payload)
                    VALUES ('production.report', @target, @idem, @payload::jsonb)
                    """, connection, transaction);
                insertCommand.Parameters.AddWithValue("target", request.Target ?? "mes.mock");
                insertCommand.Parameters.AddWithValue("idem", idempotencyKey);
                insertCommand.Parameters.AddWithValue("payload", NpgsqlTypes.NpgsqlDbType.Jsonb, payloadJson);
                await insertCommand.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
                return Results.Ok(new { idempotencyKey, inserted = true, duplicate = false });
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
            {
                // 唯一索引撞键 = 同序号报文已存在：对账兜底路径收到的是重放，返回已入账
                await transaction.RollbackAsync(CancellationToken.None);
                return Results.Ok(new { idempotencyKey = "", inserted = false, duplicate = true });
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
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

        /// <summary>LIKE 模式转义（\ % _），与 WorkOrderStore.EscapeLike 同口径。</summary>
        static string EscapeLike(string value) => value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }
}

public sealed record ReportRequest(string WorkOrderNo, int StepNo, decimal GoodQty, decimal ScrapQty, string? Target = null);
public sealed record StateRequest(string DeviceCode, string State, string? Target = null);
public sealed record ReconcileRequest(DateOnly? Date);
