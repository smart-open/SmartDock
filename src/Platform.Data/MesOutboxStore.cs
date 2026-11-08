using Npgsql;
using Platform.Abstractions;

namespace Platform.Data;

/// <summary>
/// MES Outbox 存储（6.5）：幂等落库、SKIP LOCKED 认领、状态机迁移、退避阶梯（6.6）。
/// 只用 ADO.NET（与台账/写命令同理，高频路径禁走 EF）。
/// </summary>
public sealed class MesOutboxStore : IMesOutboxStore
{
    /// <summary>6.6 退避阶梯（秒）：第 6 次起转 Dead，不再自动重发。</summary>
    public static readonly IReadOnlyList<int> BackoffSeconds = [5, 30, 120, 600, 1800];
    private const int MaxAttempts = 6;

    private readonly NpgsqlDataSource _dataSource;

    public MesOutboxStore(string connectionString)
    {
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
    }

    /// <summary>
    /// 幂等落库（6.4 铁律 1+2）：同一 idempotency_key 重复投递被唯一索引挡住。
    /// 返回是否真正新插入——业务侧据此决定是否提示「已入账」，不重复入账。
    /// </summary>
    public async Task<bool> TryEnqueueAsync(string bizType, string target, string idempotencyKey,
        string payloadJson, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO mes_outbox (biz_type, target, idempotency_key, payload)
            VALUES (@biz_type, @target, @idem, @payload::jsonb)
            ON CONFLICT (idempotency_key) DO NOTHING
            """, connection);
        command.Parameters.AddWithValue("biz_type", bizType);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("idem", idempotencyKey);
        command.Parameters.AddWithValue("payload", payloadJson);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    /// <summary>
    /// 认领一批待发送（6.5 SKIP LOCKED：多实例部署不会重复认领）。
    /// 认领即 Sending + attempts+1：进程崩溃后卡在 Sending 的行由对账兜底（6.5 状态机）。
    /// </summary>
    public async Task<IReadOnlyList<MesEnvelope>> ClaimBatchAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH cte AS (
                SELECT id FROM mes_outbox
                WHERE status = 0 AND next_at <= now()
                ORDER BY id
                LIMIT @batch
                FOR UPDATE SKIP LOCKED
            )
            UPDATE mes_outbox o
            SET status = 1, attempts = attempts + 1, updated_at = now()
            FROM cte
            WHERE o.id = cte.id
            RETURNING o.id, o.biz_type, o.target, o.idempotency_key, o.payload::text, o.attempts
            """, connection);
        command.Parameters.AddWithValue("batch", batchSize);

        var batch = new List<MesEnvelope>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            batch.Add(new MesEnvelope(
                Id: reader.GetInt64(0),
                BizType: reader.GetString(1),
                Target: reader.GetString(2),
                IdempotencyKey: reader.GetString(3),
                PayloadJson: reader.GetString(4),
                Attempts: reader.GetInt32(5)));
        }

        return batch;
    }

    /// <summary>对方确认成功 → Acked 终态（ack_value 留痕，6.4 铁律 2）。</summary>
    public Task MarkAckedAsync(long id, string? ackValue, CancellationToken cancellationToken = default)
        => UpdateAsync(id, 2, ackValue: ackValue, cancellationToken: cancellationToken);

    /// <summary>
    /// 可重试失败 → 回 Pending 并按 6.6 阶梯排 next_at；attempts 耗尽（≥6）转 Dead。
    /// </summary>
    public Task BackoffAsync(long id, int attempts, string error, CancellationToken cancellationToken = default)
    {
        if (attempts >= MaxAttempts)
        {
            return UpdateAsync(id, 4, error: error, cancellationToken: cancellationToken);
        }

        var backoffIndex = Math.Min(attempts - 1, BackoffSeconds.Count - 1);
        var delaySeconds = BackoffSeconds[backoffIndex];
        return UpdateAsync(id, 0, error: error, delaySeconds: delaySeconds, cancellationToken: cancellationToken);
    }

    /// <summary>超时/结果未知 → Suspended（转对账；对账前不自动重发，防重复入账，6.5）。</summary>
    public Task SuspectAsync(long id, string reason, CancellationToken cancellationToken = default)
        => UpdateAsync(id, 3, error: reason, cancellationToken: cancellationToken);

    /// <summary>对账确认未处理（6.5：Suspended → Pending）或人工重放 Dead（Dead → Pending，attempts 清零重走阶梯）。</summary>
    public Task RequeueAsync(long id, CancellationToken cancellationToken = default)
        => UpdateAsync(id, 0, resetAttempts: true, cancellationToken: cancellationToken);

    /// <summary>对账确认已处理（6.5：Suspended → Acked）。</summary>
    public Task ConfirmAckedAsync(long id, string? ackValue, CancellationToken cancellationToken = default)
        => UpdateAsync(id, 2, ackValue: ackValue, cancellationToken: cancellationToken);

    private async Task UpdateAsync(long id, int status, string? error = null, string? ackValue = null,
        int? delaySeconds = null, bool resetAttempts = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE mes_outbox
            SET status = @status,
                last_error = @error,
                ack_value = COALESCE(@ack_value, ack_value),
                next_at = CASE WHEN @delay::int IS NULL THEN next_at ELSE now() + make_interval(secs => @delay::int) END,
                attempts = CASE WHEN @reset THEN 0 ELSE attempts END,
                updated_at = now()
            WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("ack_value", (object?)ackValue ?? DBNull.Value);
        command.Parameters.AddWithValue("delay", (object?)delaySeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("reset", resetAttempts);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}
