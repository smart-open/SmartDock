// 02 §7.3 alarm 表 + §C1/C3 报警引擎最小实现（M3-1）。
// M3 数据源：设备连接状态变化派生（离线 raise / 恢复 clear）；C2 阈值规则引擎随后续任务。

using Npgsql;

namespace Platform.Core.Alarms;

public sealed record AlarmRow(
    long Id,
    string AlarmNo,
    long? DeviceId,
    int Level,
    string Code,
    string Message,
    DateTime RaisedAt,
    DateTime? ClearedAt,
    long? AckedBy,
    DateTime? AckedAt);

public sealed class AlarmStore(string connectionString)
{
    /// <summary>连接态变化上报：raise（未清同号复用，不重复产生行）与 clear 同事务口径。</summary>
    public async Task RaiseAsync(string alarmNo, long? deviceId, int level, string code, string message,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO alarm (alarm_no, device_id, level, code, message)
            SELECT @no, @device, @level, @code, @message
            WHERE NOT EXISTS (SELECT 1 FROM alarm WHERE alarm_no = @no AND cleared_at IS NULL)
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("no", alarmNo);
        command.Parameters.AddWithValue("device", deviceId.HasValue ? deviceId.Value : DBNull.Value);
        command.Parameters.AddWithValue("level", level);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("message", message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAsync(string alarmNo, CancellationToken cancellationToken = default)
    {
        const string sql = "UPDATE alarm SET cleared_at = now() WHERE alarm_no = @no AND cleared_at IS NULL";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("no", alarmNo);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>报警确认（C3）：记录确认人与时间；只允许确认未清报警。</summary>
    public async Task<bool> AckAsync(long alarmId, long operatorId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE alarm SET acked_by = @op, acked_at = now()
            WHERE id = @id AND cleared_at IS NULL AND acked_by IS NULL
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", alarmId);
        command.Parameters.AddWithValue("op", operatorId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>activeOnly=true 查未清（报警页主视图），false 查近 7 天历史。</summary>
    public async Task<IReadOnlyList<AlarmRow>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default)
    {
        var sql = activeOnly
            ? "SELECT id, alarm_no, device_id, level, code, message, raised_at, cleared_at, acked_by, acked_at FROM alarm WHERE cleared_at IS NULL ORDER BY level DESC, raised_at DESC LIMIT 500"
            : "SELECT id, alarm_no, device_id, level, code, message, raised_at, cleared_at, acked_by, acked_at FROM alarm WHERE raised_at > now() - interval '7 days' ORDER BY raised_at DESC LIMIT 500";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<AlarmRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AlarmRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetDateTime(9)));
        }
        return rows;
    }
}
