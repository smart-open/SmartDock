// 02 §3.7.1 存储：定义全量读（缓存回源）、范围解析（窄覆盖宽）、变更落库 + 版本号推进。
// NpgsqlDataSource 由 Host 组合根注入（与 ReportStore 同口径，复用全局连接池）。

using Npgsql;
using Platform.Abstractions.Features;

namespace Platform.Core.Features;

public interface IFeatureStore
{
    IReadOnlyDictionary<string, FeatureDefinition> GetAllDefinitions();

    /// <summary>范围解析：line &gt; workshop &gt; factory &gt; global（3.7.1），无覆盖返回 null（用 default）。</summary>
    bool? Resolve(string key, FeatureScope? scope);

    long GetVersion();

    /// <summary>写入范围覆盖（Planner 六步通过后调用）并推进版本号。</summary>
    Task SaveScopeOverrideAsync(string key, string scopeType, string scopeId, bool enabled,
        string reason, string? ticketNo, long updatedBy, CancellationToken cancellationToken = default);

    /// <summary>查互斥/依赖键的当前生效值（Planner 步骤 2/3 用）。</summary>
    Task<IReadOnlyDictionary<string, bool>> ResolveKeysAsync(
        IEnumerable<string> keys, CancellationToken cancellationToken = default);

    /// <summary>批量覆盖（3.7.5 batch）：一个事务落全部变更，版本号只推进一次。</summary>
    Task SaveScopeOverridesAsync(
        IReadOnlyList<(string Key, bool Enabled, string Reason, string? TicketNo)> changes,
        long updatedBy, CancellationToken cancellationToken = default);

    /// <summary>回滚（3.7.5）：删该键最近一条覆盖（恢复默认值）并推进版本号；无覆盖返回 false。</summary>
    Task<bool> DeleteLastScopeOverrideAsync(string key, long updatedBy, CancellationToken cancellationToken = default);
}

public sealed class FeatureStore : IFeatureStore
{
    private readonly NpgsqlDataSource _dataSource;

    public FeatureStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public IReadOnlyDictionary<string, FeatureDefinition> GetAllDefinitions()
    {
        using var command = _dataSource.CreateCommand(
            """
            SELECT flag_key, display_name, grp, default_enabled, off_behavior,
                   requires, conflicts, runtime_scope, edition_min, requires_plugin, always_on
            FROM feature_flag ORDER BY sort_no
            """);
        using var reader = command.ExecuteReader();
        var definitions = new Dictionary<string, FeatureDefinition>(70, StringComparer.Ordinal);
        while (reader.Read())
        {
            definitions[reader.GetString(0)] = new FeatureDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetString(4)[0],
                MapArray(reader, 5),
                MapArray(reader, 6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetBoolean(9),
                reader.GetBoolean(10));
        }

        return definitions;
    }

    public bool? Resolve(string key, FeatureScope? scope)
    {
        // 只解析与请求范围匹配的覆盖层：global 恒生效，factory/workshop/line 各自
        // 精确匹配 scope_id。否则任意一条 line 级覆盖会压过全平台所有范围
        //（02 §3.7.1「line > workshop > factory > global」是层级优先序，不是无差别压序）
        using var command = _dataSource.CreateCommand(
            """
            -- 窄覆盖宽：line(3) > workshop(2) > factory(1) > global(0)，
            -- 同层取 effective_from 最新且当前生效的一条；无匹配 = 无覆盖（调用方用 default）
            SELECT enabled FROM feature_scope
            WHERE flag_key = @key
              AND (
                    scope_type = 'global'
                    OR (scope_type = 'factory'  AND @factory  IS NOT NULL AND scope_id = @factory)
                    OR (scope_type = 'workshop' AND @workshop IS NOT NULL AND scope_id = @workshop)
                    OR (scope_type = 'line'     AND @line     IS NOT NULL AND scope_id = @line)
                  )
              AND (effective_from IS NULL OR effective_from <= now())
              AND (effective_to IS NULL OR effective_to > now())
            ORDER BY CASE scope_type WHEN 'line' THEN 3 WHEN 'workshop' THEN 2
                                     WHEN 'factory' THEN 1 ELSE 0 END DESC,
                     effective_from DESC NULLS LAST,
                     id DESC
            LIMIT 1
            """);
        command.Parameters.AddWithValue("key", key);
        var scopeValue = scope ?? new FeatureScope(string.Empty);
        // 范围参数必须显式 NpgsqlDbType.Text：AddWithValue 塞 DBNull 时 Npgsql 不发类型，
        // Postgres 对 "@p IS NOT NULL AND scope_id = @p" 推断不出 $n 类型（42P08）
        command.Parameters.Add(new NpgsqlParameter("factory", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = string.IsNullOrEmpty(scopeValue.Factory) ? DBNull.Value : scopeValue.Factory,
        });
        command.Parameters.Add(new NpgsqlParameter("workshop", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)scopeValue.Workshop ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("line", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)scopeValue.Line ?? DBNull.Value,
        });
        using var reader = command.ExecuteReader();
        return reader.Read() ? reader.GetBoolean(0) : null;
    }

    public long GetVersion()
    {
        using var command = _dataSource.CreateCommand("SELECT version FROM feature_version WHERE id = 1");
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public async Task SaveScopeOverrideAsync(string key, string scopeType, string scopeId, bool enabled,
        string reason, string? ticketNo, long updatedBy, CancellationToken cancellationToken = default)
    {
        // 覆盖与版本号推进同事务：读侧要么看到旧值+旧版本，要么新值+新版本，不会错位
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO feature_scope (flag_key, scope_type, scope_id, enabled, reason, ticket_no, updated_by)
            VALUES (@key, @scope_type, @scope_id, @enabled, @reason, @ticket_no, @updated_by)
            """, connection, transaction);
        insert.Parameters.AddWithValue("key", key);
        insert.Parameters.AddWithValue("scope_type", scopeType);
        insert.Parameters.AddWithValue("scope_id", scopeId);
        insert.Parameters.AddWithValue("enabled", enabled);
        insert.Parameters.AddWithValue("reason", reason);
        insert.Parameters.AddWithValue("ticket_no", (object?)ticketNo ?? DBNull.Value);
        insert.Parameters.AddWithValue("updated_by", updatedBy);
        await insert.ExecuteNonQueryAsync(cancellationToken);

        await using var bump = new NpgsqlCommand(
            "UPDATE feature_version SET version = version + 1, updated_at = now() WHERE id = 1",
            connection, transaction);
        await bump.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, bool>> ResolveKeysAsync(
        IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            // 复用同步 Resolve（单键查询）；Planner 是低频管理操作，无需批量优化
            var resolved = Resolve(key, null);
            if (resolved is { } value)
            {
                result[key] = value;
            }
        }

        await Task.CompletedTask;
        return result;
    }

    public async Task SaveScopeOverridesAsync(
        IReadOnlyList<(string Key, bool Enabled, string Reason, string? TicketNo)> changes,
        long updatedBy, CancellationToken cancellationToken = default)
    {
        // 批量变更与版本推进同事务：全部成功或全部不落（3.7.4「批量变更生成一个 change_id」
        // 的 M3 简化——同一批共享 reason/ticket，回滚按键撤销）
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (key, enabled, reason, ticketNo) in changes)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO feature_scope (flag_key, scope_type, scope_id, enabled, reason, ticket_no, updated_by)
                VALUES (@key, 'global', '*', @enabled, @reason, @ticket_no, @updated_by)
                """, connection, transaction);
            insert.Parameters.AddWithValue("key", key);
            insert.Parameters.AddWithValue("enabled", enabled);
            insert.Parameters.AddWithValue("reason", reason);
            insert.Parameters.AddWithValue("ticket_no", (object?)ticketNo ?? DBNull.Value);
            insert.Parameters.AddWithValue("updated_by", updatedBy);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var bump = new NpgsqlCommand(
            "UPDATE feature_version SET version = version + 1, updated_at = now() WHERE id = 1",
            connection, transaction);
        await bump.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> DeleteLastScopeOverrideAsync(string key, long updatedBy, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 取最新一条覆盖的 id 再删：回滚 = 撤销最近一次变更、恢复默认生效值
        await using var select = new NpgsqlCommand(
            """
            SELECT id FROM feature_scope
            WHERE flag_key = @key AND scope_type = 'global' AND scope_id = '*'
            ORDER BY id DESC LIMIT 1
            """, connection, transaction);
        select.Parameters.AddWithValue("key", key);
        var lastId = await select.ExecuteScalarAsync(cancellationToken);
        if (lastId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using var delete = new NpgsqlCommand(
            "DELETE FROM feature_scope WHERE id = @id", connection, transaction);
        delete.Parameters.AddWithValue("id", (long)lastId);
        await delete.ExecuteNonQueryAsync(cancellationToken);

        await using var bump = new NpgsqlCommand(
            "UPDATE feature_version SET version = version + 1, updated_at = now() WHERE id = 1",
            connection, transaction);
        await bump.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // text[] 列直接取 Npgsql 数组映射，避免手工 split
    private static string[] MapArray(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? [] : (string[])reader.GetValue(ordinal);
}
