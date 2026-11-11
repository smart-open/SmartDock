using Npgsql;
using NpgsqlTypes;

namespace Platform.Data;

/// <summary>
/// 工单域存储（7.2 业务组）：工单/工序/报工。
/// 报工必须在**同一事务**里完成「报工行 + 工单完工数累加 + Outbox 报文」三件事——
/// 任一失败整体回滚，保证「产量账」与「MES 报文」同生共死（0.4 先落库再动作）。
/// </summary>
public sealed class WorkOrderStore
{
    private readonly NpgsqlDataSource _dataSource;

    public WorkOrderStore(string connectionString)
    {
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
    }

    public sealed record WorkOrderRow(
        long Id, string OrderNo, string ProductCode, decimal PlanQty, decimal CompletedQty,
        short Status, string? MesVersion);

    /// <summary>工单下发（6.2 接口 1 的落库侧）：MES/人工创建，幂等由 order_no UNIQUE 兜底。</summary>
    public async Task<long> CreateOrderAsync(string orderNo, string productCode, decimal planQty,
        int? mesVersion, IReadOnlyList<(int StepNo, string StepName, decimal PlanQty)> steps,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var insertOrder = new NpgsqlCommand("""
                INSERT INTO work_order (order_no, product_code, plan_qty, mes_version)
                VALUES (@no, @product, @plan, @mes_version)
                ON CONFLICT (order_no) DO UPDATE SET mes_version = EXCLUDED.mes_version
                RETURNING id
                """, connection, transaction);
            insertOrder.Parameters.AddWithValue("no", orderNo);
            insertOrder.Parameters.AddWithValue("product", productCode);
            insertOrder.Parameters.AddWithValue("plan", planQty);
            insertOrder.Parameters.AddWithValue("mes_version", (object?)mesVersion ?? DBNull.Value);
            var orderId = (long)(await insertOrder.ExecuteScalarAsync(cancellationToken))!;

            foreach (var (stepNo, stepName, stepPlanQty) in steps)
            {
                await using var insertStep = new NpgsqlCommand("""
                    INSERT INTO wo_step (order_id, step_no, step_name, plan_qty)
                    VALUES (@order_id, @step_no, @name, @plan)
                    ON CONFLICT (order_id, step_no) DO UPDATE SET step_name = EXCLUDED.step_name, plan_qty = EXCLUDED.plan_qty
                    """, connection, transaction);
                insertStep.Parameters.AddWithValue("order_id", orderId);
                insertStep.Parameters.AddWithValue("step_no", stepNo);
                insertStep.Parameters.AddWithValue("name", stepName);
                insertStep.Parameters.AddWithValue("plan", stepPlanQty);
                await insertStep.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return orderId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<WorkOrderRow?> GetByOrderNoAsync(string orderNo, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, order_no, product_code, plan_qty, completed_qty, status, mes_version::text
            FROM work_order WHERE order_no = @no
            """, connection);
        command.Parameters.AddWithValue("no", orderNo);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WorkOrderRow(
            Id: reader.GetInt64(0),
            OrderNo: reader.GetString(1),
            ProductCode: reader.GetString(2),
            PlanQty: reader.GetDecimal(3),
            CompletedQty: reader.GetDecimal(4),
            Status: reader.GetInt16(5),
            MesVersion: reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    /// <summary>
    /// 报工（6.2 接口 3）：一个事务写三处——报工行、工单/工序完工数、Outbox 报文。
    /// 幂等键在本方法锁内组装（workOrderNo + stepNo + reportSeq）——
    /// 锁外预查 seq 会与并发报工错位，键必须与落库 seq 同源（6.4 铁律 1）。
    /// 返回（报工行 id, report_seq）。
    /// </summary>
    public async Task<(long ReportId, int ReportSeq)> SubmitReportAsync(
        long orderId, int stepNo, decimal goodQty, decimal scrapQty, long operatorId, long? deviceId,
        string source, string orderNo, string target,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // 锁工单行：并发报工时 completed_qty 累加与 seq 生成都在同一把锁后串行，防脏累加
            await using var lockOrder = new NpgsqlCommand(
                "SELECT status FROM work_order WHERE id = @id FOR UPDATE", connection, transaction);
            lockOrder.Parameters.AddWithValue("id", orderId);
            var orderStatus = (short)((await lockOrder.ExecuteScalarAsync(cancellationToken)) ?? (short)-1);
            if (!CanReport(orderStatus))
            {
                throw new InvalidOperationException($"order {orderId} not active (status={orderStatus})");
            }

            // seq 在锁内生成，消除并发窗口
            await using var seqCommand = new NpgsqlCommand("""
                SELECT COALESCE(MAX(report_seq), 0) + 1 FROM production_report
                WHERE order_id = @order_id AND step_no = @step_no
                """, connection, transaction);
            seqCommand.Parameters.AddWithValue("order_id", orderId);
            seqCommand.Parameters.AddWithValue("step_no", stepNo);
            var reportSeq = (int)(await seqCommand.ExecuteScalarAsync(cancellationToken))!;
            // 幂等键与 seq 同源同事务：并发报工时锁保证 seq 与键严格一致
            // 注意：不能用 MesIdempotencyKeyBuilder.ProductionReport——那是进程内存计数器，
            // 重启后从 1 重计会与历史键撞车（新报工被唯一索引误判重而静默丢失，6.4 铁律 1）
            var idempotencyKey = $"production.report:{orderNo}:{stepNo}:{reportSeq}";

            await using var insertReport = new NpgsqlCommand("""
                INSERT INTO production_report (order_id, step_no, report_seq, good_qty, scrap_qty, operator_id, device_id, source)
                VALUES (@order_id, @step_no, @seq, @good, @scrap, @operator_id, @device_id, @source)
                RETURNING id
                """, connection, transaction);
            insertReport.Parameters.AddWithValue("order_id", orderId);
            insertReport.Parameters.AddWithValue("step_no", stepNo);
            insertReport.Parameters.AddWithValue("seq", reportSeq);
            insertReport.Parameters.AddWithValue("good", goodQty);
            insertReport.Parameters.AddWithValue("scrap", scrapQty);
            insertReport.Parameters.AddWithValue("operator_id", operatorId);
            insertReport.Parameters.AddWithValue("device_id", (object?)deviceId ?? DBNull.Value);
            insertReport.Parameters.AddWithValue("source", source);
            var reportId = (long)(await insertReport.ExecuteScalarAsync(cancellationToken))!;

            await using var updateQty = new NpgsqlCommand("""
                UPDATE work_order SET completed_qty = completed_qty + @good WHERE id = @order_id;
                UPDATE wo_step
                SET good_qty = good_qty + @good, scrap_qty = scrap_qty + @scrap,
                    status = CASE WHEN good_qty + @good >= plan_qty THEN 2 ELSE 1 END
                WHERE order_id = @order_id AND step_no = @step_no
                """, connection, transaction);
            updateQty.Parameters.AddWithValue("good", goodQty);
            updateQty.Parameters.AddWithValue("scrap", scrapQty);
            updateQty.Parameters.AddWithValue("order_id", orderId);
            updateQty.Parameters.AddWithValue("step_no", stepNo);
            await updateQty.ExecuteNonQueryAsync(cancellationToken);

            // 北向报文同事务入 Outbox：报工落库成功 ⇒ 报文必在（断网时 Worker 退避重试，不丢账）
            var payload = new ReportPayload(orderId, stepNo, goodQty, scrapQty, reportSeq, source);
            await using var insertOutbox = new NpgsqlCommand("""
                INSERT INTO mes_outbox (biz_type, target, idempotency_key, payload)
                VALUES ('production.report', @target, @idem, @payload::jsonb)
                ON CONFLICT (idempotency_key) DO NOTHING
                """, connection, transaction);
            insertOutbox.Parameters.AddWithValue("target", target);
            insertOutbox.Parameters.AddWithValue("idem", idempotencyKey);
            insertOutbox.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(payload));
            await insertOutbox.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return (reportId, reportSeq);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 6.2 报工状态闸：仅待开工(0)/进行中(1)可报工。SubmitReportAsync 锁内判据的唯一来源
    /// ——状态机扩展（如新增"暂停可报"）只改这里，端点/测试自动同源。
    /// </summary>
    public static bool CanReport(short orderStatus) => orderStatus is 0 or 1;

    public sealed record ReportPayload(long OrderId, int StepNo, decimal GoodQty, decimal ScrapQty, int ReportSeq, string Source);

    /// <summary>工单状态变更（6.2 接口 2）：更新状态 + 返回平台侧递增 seq（回传幂等键的一部分）。</summary>
    public async Task<int> ChangeStatusAsync(string orderNo, short newStatus, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var lockCommand = new NpgsqlCommand(
                "SELECT id, status FROM work_order WHERE order_no = @no FOR UPDATE", connection, transaction);
            lockCommand.Parameters.AddWithValue("no", orderNo);
            await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException($"order not found: {orderNo}");
            }
            var orderId = reader.GetInt64(0);
            var currentStatus = reader.GetInt16(1);
            await reader.DisposeAsync();

            if (currentStatus == newStatus)
            {
                // 幂等：同状态重复请求直接返回当前序号，不再产新报文（状态变化才触发，6.2 接口 2）
                await transaction.RollbackAsync(CancellationToken.None);
                await using var seqRead = new NpgsqlCommand("""
                    SELECT COUNT(*) FROM mes_outbox
                    WHERE biz_type = 'work.order.status' AND idempotency_key LIKE @like ESCAPE '\'
                    """, connection);
                // 与下方新报文分支同口径：LIKE 通配符转义，防 order_no 含 %/_ 时 seq 错乱
                seqRead.Parameters.AddWithValue("like",
                    $"work.order.status:{EscapeLike(orderNo)}:{newStatus}:%");
                // COUNT(*) 返回 bigint（Int64），显式转 int 而非直接 cast（直接 (int) 装 Int64 会 InvalidCastException）
                var existingCount = (long)(await seqRead.ExecuteScalarAsync(cancellationToken))!;
                return (int)existingCount;
            }

            await using var updateCommand = new NpgsqlCommand(
                "UPDATE work_order SET status = @status WHERE id = @id", connection, transaction);
            updateCommand.Parameters.AddWithValue("status", newStatus);
            updateCommand.Parameters.AddWithValue("id", orderId);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);

            // 状态回传报文同事务入 Outbox，幂等键 = workOrderNo + status + seq（6.2 接口 2）
            await using var seqCommand = new NpgsqlCommand("""
                SELECT COUNT(*) FROM mes_outbox
                WHERE biz_type = 'work.order.status' AND idempotency_key LIKE @like ESCAPE '\'
                """, connection, transaction);
            // order_no 是业务透传值，可能含 % 或 _（LIKE 通配符）：不转义会把别的单号
            // 的报文计进来，seq 错乱后又反撞唯一索引。ESCAPE 前缀精确匹配。
            seqCommand.Parameters.AddWithValue("like",
                $"work.order.status:{EscapeLike(orderNo)}:{newStatus}:%");
            var priorMessageCount = (long)(await seqCommand.ExecuteScalarAsync(cancellationToken))!;
            var seq = (int)priorMessageCount + 1;

            await using var insertOutbox = new NpgsqlCommand("""
                INSERT INTO mes_outbox (biz_type, target, idempotency_key, payload)
                VALUES ('work.order.status', @target, @idem, @payload::jsonb)
                """, connection, transaction);
            insertOutbox.Parameters.AddWithValue("target", "mes.mock");
            insertOutbox.Parameters.AddWithValue("idem", $"work.order.status:{orderNo}:{newStatus}:{seq}");
            insertOutbox.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb,
                System.Text.Json.JsonSerializer.Serialize(new StatusPayload(orderNo, newStatus, seq)));
            await insertOutbox.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return seq;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public sealed record StatusPayload(string OrderNo, short Status, int Seq);

    /// <summary>LIKE 模式转义：\ % _ 三字符。与 ESCAPE '\' 搭配（同文件两处状态 seq 计数共用）。</summary>
    private static string EscapeLike(string value) => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}
