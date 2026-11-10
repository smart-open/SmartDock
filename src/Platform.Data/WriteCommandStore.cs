using Npgsql;
using Platform.Abstractions;

namespace Platform.Data;

/// <summary>
/// 写命令落库（5.6 闸 3：先落意图再执行；状态机 PENDING → SENT → DONE/FAILED/REJECTED）。
/// 只用 ADO.NET（与台账同理，禁走 EF）。
/// </summary>
public sealed class WriteCommandStore
{
    private readonly NpgsqlDataSource _dataSource;

    public WriteCommandStore(string connectionString)
    {
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
    }

    /// <summary>闸 3：写入意图（PENDING），返回命令 ID。</summary>
    public async Task<long> InsertPendingAsync(long pointId, double value, string operatorName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO write_command (point_id, device_id, value_num, operator)
            SELECT @point_id, dp.device_id, @value, @operator
            FROM device_point dp WHERE dp.id = @point_id
            RETURNING id
            """, connection);
        command.Parameters.AddWithValue("point_id", pointId);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("operator", operatorName);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long id ? id : throw new InvalidOperationException($"unknown point {pointId}");
    }

    public async Task MarkRejectedAsync(long commandId, string reason, CancellationToken cancellationToken = default)
        => await UpdateAsync(commandId, "REJECTED", rejectReason: reason, cancellationToken: cancellationToken);

    public async Task MarkSentAsync(long commandId, CancellationToken cancellationToken = default)
        => await UpdateAsync(commandId, "SENT", cancellationToken: cancellationToken);

    /// <summary>终态：DONE（回读通过）或 FAILED（error 留痕）。</summary>
    public async Task FinishAsync(long commandId, bool verified, string? error, double? readBackValue,
        CancellationToken cancellationToken = default)
        => await UpdateAsync(commandId, verified ? "DONE" : "FAILED", error: error,
            readBackValue: readBackValue, cancellationToken: cancellationToken);

    private async Task UpdateAsync(long commandId, string status, string? rejectReason = null,
        string? error = null, double? readBackValue = null, CancellationToken cancellationToken = default)
    {
        // 状态机守卫：终态只能从合法前态进入（DONE→SENT 等乱序覆盖一律拒绝）。
        // 守卫不命中（已是终态）静默跳过——WriteGuard 异常兜底可能对同一命令补写 FAILED
        var allowedFrom = status switch
        {
            "SENT" => "('PENDING')",
            "DONE" or "FAILED" => "('PENDING','SENT')",
            "REJECTED" => "('PENDING','SENT')",
            _ => throw new InvalidOperationException($"unknown write_command status: {status}"),
        };

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE write_command
            SET status = @status,
                reject_reason = COALESCE(@reject_reason, reject_reason),
                error = @error,
                value_readback = @read_back_value,
                sent_at = CASE WHEN @status = 'SENT' THEN now() ELSE sent_at END,
                finished_at = CASE WHEN @status IN ('DONE', 'FAILED', 'REJECTED') THEN now() ELSE finished_at END
            WHERE id = @id AND status IN {allowedFrom}
            """, connection);
        command.Parameters.AddWithValue("id", commandId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("reject_reason", (object?)rejectReason ?? DBNull.Value);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("read_back_value", (object?)readBackValue ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}
