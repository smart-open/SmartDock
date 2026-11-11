using Npgsql;
using NpgsqlTypes;

namespace Platform.Data;

/// <summary>
/// MES 对账与兜底（6.6）：
/// ① Sending 卡死回置——Worker 崩溃后记录停在 Sending，扫描超 10min 未终态的行回 Pending/Dead；
/// ② 每日对账——按 bizType 汇总终态分布，未闭环（Suspended+Dead）超阈值告警，结果落 mes_reconcile。
/// 只用 ADO.NET（同平台数据层口径）。
/// </summary>
public sealed class MesReconcileStore
{
    private const int MaxAttempts = 6;

    private readonly NpgsqlDataSource _dataSource;

    public MesReconcileStore(string connectionString)
    {
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
    }

    /// <summary>
    /// 认领后崩溃兜底（6.6 引文）：回置 Sending 超时的行。返回回置条数。
    /// attempts 已在认领时 +1，所以这里按 attempts 与上限比较，超限直接 Dead。
    /// </summary>
    public async Task<int> RequeueStuckSendingAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // 先把超限的转 Dead（不再自动重发）
        await using var toDead = new NpgsqlCommand("""
            UPDATE mes_outbox
            SET status = 4, last_error = COALESCE(last_error, 'stuck in sending: exceeded max attempts'),
                updated_at = now()
            WHERE status = 1 AND updated_at < now() - interval '10 minutes' AND attempts >= @max
            """, connection);
        toDead.Parameters.AddWithValue("max", MaxAttempts);
        var deadCount = await toDead.ExecuteNonQueryAsync(cancellationToken);

        // 未超限的回 Pending（下一轮 Worker 认领重推；attempts 已计，走退避阶梯）
        await using var toPending = new NpgsqlCommand("""
            UPDATE mes_outbox
            SET status = 0, last_error = COALESCE(last_error, 'stuck in sending: worker crashed?'),
                updated_at = now()
            WHERE status = 1 AND updated_at < now() - interval '10 minutes'
            """, connection);
        var pendingCount = await toPending.ExecuteNonQueryAsync(cancellationToken);

        return deadCount + pendingCount;
    }

    public sealed record ReconcileResult(
        DateOnly BizDate, string BizType, int TotalSent, int TotalAcked,
        int TotalSuspended, int TotalDead, int TotalPending, int DiffCount, decimal DiffRatio, bool Alert);

    /// <summary>
    /// 每日对账（6.6 对账任务）：按 bizType 汇总当日 Outbox 终态。
    /// 「对方已收条数」平台侧以 Acked 计（真实 MES 对账需对方提供查询接口，随适配器扩展 ReconcileAsync）。
    /// 幂等：UNIQUE(biz_date, biz_type) upsert，重跑覆盖当日结果。
    /// </summary>
    public async Task<IReadOnlyList<ReconcileResult>> ReconcileAsync(DateOnly bizDate, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // 当日所有 bizType 的终态分布（以 created_at 定业务日；重试跨日的报文仍归入账日）
        await using var select = new NpgsqlCommand("""
            SELECT biz_type,
                   COUNT(*)::int,
                   COUNT(*) FILTER (WHERE status = 2)::int,
                   COUNT(*) FILTER (WHERE status = 3)::int,
                   COUNT(*) FILTER (WHERE status = 4)::int,
                   COUNT(*) FILTER (WHERE status IN (0, 1))::int
            FROM mes_outbox
            WHERE created_at >= @day_start AND created_at < @day_end
            GROUP BY biz_type
            """, connection);
        select.Parameters.AddWithValue("day_start", NpgsqlDbType.Date, bizDate.ToDateTime(TimeOnly.MinValue));
        select.Parameters.AddWithValue("day_end", NpgsqlDbType.Date, bizDate.AddDays(1).ToDateTime(TimeOnly.MinValue));

        var results = new List<ReconcileResult>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var bizType = reader.GetString(0);
                var totalSent = reader.GetInt32(1);
                var acked = reader.GetInt32(2);
                var suspended = reader.GetInt32(3);
                var dead = reader.GetInt32(4);
                var pending = reader.GetInt32(5);
                var diffCount = suspended + dead;

                // 6.6 对账任务 3：差异 > 0.5% 或 > 10 条 → 告警（判据唯一来源 ReconcileAlertPolicy）
                var diffRatio = totalSent == 0 ? 0m : Math.Round(diffCount / (decimal)totalSent, 4);
                var alert = ReconcileAlertPolicy.ShouldAlert(diffCount, totalSent);

                results.Add(new ReconcileResult(bizDate, bizType, totalSent, acked, suspended, dead, pending, diffCount, diffRatio, alert));
            }
        }

        foreach (var result in results)
        {
            // 未闭环明细（msg id + 当前状态）落 detail，差异可定位（04 验收 #22）
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO mes_reconcile (biz_date, biz_type, total_sent, total_acked, total_suspended,
                                           total_dead, total_pending, diff_count, diff_ratio, alert, detail)
                VALUES (@date, @type, @sent, @acked, @susp, @dead, @pend, @diff, @ratio, @alert,
                        (SELECT jsonb_agg(jsonb_build_object('id', id, 'status', status, 'error', last_error))
                         FROM mes_outbox WHERE created_at >= @day_start AND created_at < @day_end
                           AND biz_type = @type AND status IN (3, 4)))
                ON CONFLICT (biz_date, biz_type) DO UPDATE SET
                    total_sent = EXCLUDED.total_sent, total_acked = EXCLUDED.total_acked,
                    total_suspended = EXCLUDED.total_suspended, total_dead = EXCLUDED.total_dead,
                    total_pending = EXCLUDED.total_pending, diff_count = EXCLUDED.diff_count,
                    diff_ratio = EXCLUDED.diff_ratio, alert = EXCLUDED.alert, detail = EXCLUDED.detail,
                    created_at = now()
                """, connection);
            upsert.Parameters.AddWithValue("date", NpgsqlDbType.Date, result.BizDate.ToDateTime(TimeOnly.MinValue));
            upsert.Parameters.AddWithValue("type", result.BizType);
            upsert.Parameters.AddWithValue("sent", result.TotalSent);
            upsert.Parameters.AddWithValue("acked", result.TotalAcked);
            upsert.Parameters.AddWithValue("susp", result.TotalSuspended);
            upsert.Parameters.AddWithValue("dead", result.TotalDead);
            upsert.Parameters.AddWithValue("pend", result.TotalPending);
            upsert.Parameters.AddWithValue("diff", result.DiffCount);
            upsert.Parameters.AddWithValue("ratio", NpgsqlDbType.Numeric, result.DiffRatio);
            upsert.Parameters.AddWithValue("alert", result.Alert);
            upsert.Parameters.AddWithValue("day_start", NpgsqlDbType.Date, result.BizDate.ToDateTime(TimeOnly.MinValue));
            upsert.Parameters.AddWithValue("day_end", NpgsqlDbType.Date, result.BizDate.AddDays(1).ToDateTime(TimeOnly.MinValue));
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        return results;
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}

/// <summary>
/// 6.6 对账任务 3 的告警判据唯一来源：差异行数 > 10 条 或 差异占比 > 0.5%
/// （占比四舍五入到 4 位小数后比较，与 mes_reconcile.diff_ratio 落库精度一致）。
/// Extract 自 MesReconcileStore 内联判定——阈值调整/告警口径演进只改这里。
/// </summary>
public static class ReconcileAlertPolicy
{
    public const int MaxDiffCountWithoutAlert = 10;
    public const decimal AlertRatioThreshold = 0.005m;

    public static bool ShouldAlert(int diffCount, int totalSent)
    {
        var diffRatio = totalSent == 0 ? 0m : Math.Round(diffCount / (decimal)totalSent, 4);
        return diffCount > MaxDiffCountWithoutAlert || diffRatio > AlertRatioThreshold;
    }
}
