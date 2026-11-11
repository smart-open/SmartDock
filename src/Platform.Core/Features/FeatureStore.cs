// 02 §3.7.1 存储：定义全量读（缓存回源）、范围解析（窄覆盖宽，覆盖行缓存后纯内存匹配）、
// 变更落库 + 版本号推进。
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

    // 覆盖行缓存：与 FeatureGate 定义缓存同一套口径（版本号 + TTL + 竞态复核 + 信任窗）。
    // Resolve 原实现每次查库——Snapshot 对 70 键逐个 Get 即 70 次/快照（S28.5 性能债）；
    // 改为缓存全量覆盖行后按层级在内存匹配，快照热路径零查库
    private readonly object _overrideCacheLock = new();
    private (long Version, DateTimeOffset LoadedAt, IReadOnlyList<FeatureScopeOverrideRow> Rows)? _overrideCache;
    private static readonly TimeSpan OverrideCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OverrideVersionTrustWindow = TimeSpan.FromSeconds(5);

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
        return FeatureScopeResolver.Resolve(GetScopeOverridesCached(), key, scope, DateTime.UtcNow);
    }

    private IReadOnlyList<FeatureScopeOverrideRow> GetScopeOverridesCached()
    {
        lock (_overrideCacheLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_overrideCache is { } cached)
            {
                // 信任窗内免查版本号：Resolve 在快照里逐键调用，每次查版本号会把
                // 内存匹配的优势吃光。窗口内变更最坏延迟 5s（远小于原 30s 口径）
                if (now - cached.LoadedAt < OverrideVersionTrustWindow)
                {
                    return cached.Rows;
                }

                if (now - cached.LoadedAt < OverrideCacheTtl && cached.Version == GetVersion())
                {
                    _overrideCache = (cached.Version, now, cached.Rows);
                    return cached.Rows;
                }
            }

            var versionBefore = GetVersion();
            var rows = GetAllScopeOverrides();
            var versionAfter = GetVersion();
            if (versionBefore != versionAfter)
            {
                // 回源期间有写提交：本次不缓存，退回 TTL 兜底（与 FeatureGate 定义缓存同策略）
                return rows;
            }

            _overrideCache = (versionAfter, now, rows);
            return rows;
        }
    }

    private void InvalidateOverrideCache()
    {
        lock (_overrideCacheLock)
        {
            _overrideCache = null;
        }
    }

    public long GetVersion()
    {
        using var command = _dataSource.CreateCommand("SELECT version FROM feature_version WHERE id = 1");
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>覆盖行全量读（缓存回源用；覆盖行总量 = 能力数 × 层级数，量级很小）。</summary>
    private IReadOnlyList<FeatureScopeOverrideRow> GetAllScopeOverrides()
    {
        using var command = _dataSource.CreateCommand(
            """
            SELECT id, flag_key, scope_type, scope_id, enabled, effective_from, effective_to
            FROM feature_scope
            """);
        using var reader = command.ExecuteReader();
        var rows = new List<FeatureScopeOverrideRow>();
        while (reader.Read())
        {
            rows.Add(new FeatureScopeOverrideRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6)));
        }

        return rows;
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
        // 本实例写库后缓存必然过期：立即失效（跨实例写入靠版本号/TTL 兜底）
        InvalidateOverrideCache();
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
        InvalidateOverrideCache();
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
        InvalidateOverrideCache();
        return true;
    }

    // text[] 列直接取 Npgsql 数组映射，避免手工 split
    private static string[] MapArray(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? [] : (string[])reader.GetValue(ordinal);
}

/// <summary>feature_scope 覆盖行（Resolve 内存匹配的输入；字段与表列一一对应）。</summary>
public sealed record FeatureScopeOverrideRow(
    long Id,
    string Key,
    string ScopeType,
    string ScopeId,
    bool Enabled,
    DateTime? EffectiveFrom,
    DateTime? EffectiveTo);

/// <summary>
/// 3.7.1 范围解析的纯函数实现（可单测）：
/// 只解析与请求范围匹配的覆盖层——global 恒生效，factory/workshop/line 各自精确匹配
/// scope_id（层级是优先序不是无差别压序：任意一条 line 覆盖不得压过全平台所有范围）；
/// 同层取 effective_from 最新且当前生效的一条；无匹配 = 无覆盖（调用方用 default）。
/// </summary>
public static class FeatureScopeResolver
{
    public static bool? Resolve(
        IReadOnlyList<FeatureScopeOverrideRow> rows, string key, FeatureScope? scope, DateTime utcNow)
    {
        var scopeValue = scope ?? new FeatureScope(string.Empty);
        FeatureScopeOverrideRow? best = null;

        foreach (var row in rows)
        {
            if (row.Key != key || !MatchesScope(row, scopeValue) || !IsEffective(row, utcNow))
            {
                continue;
            }

            // 排序口径与原 SQL 完全一致：层级降序 → effective_from 降序（NULL 最后）→ id 降序。
            // id 平局裁决不可省：同层同 effective_from 的两条覆盖（回滚后重写的常见形态）
            // 没有 id DESC 就取错行（单测 SameRankSameFromHigherIdWins 抓到的回归）
            if (best is null || WinsOver(row, best))
            {
                best = row;
            }
        }

        return best?.Enabled;
    }

    private static bool WinsOver(FeatureScopeOverrideRow candidate, FeatureScopeOverrideRow incumbent)
    {
        var candidateRank = ScopeRank(candidate.ScopeType);
        var incumbentRank = ScopeRank(incumbent.ScopeType);
        if (candidateRank != incumbentRank)
        {
            return candidateRank > incumbentRank;
        }

        var byEffectiveFrom = CompareEffectiveFrom(candidate.EffectiveFrom, incumbent.EffectiveFrom);
        if (byEffectiveFrom != 0)
        {
            return byEffectiveFrom > 0;
        }

        return candidate.Id > incumbent.Id;
    }

    private static bool MatchesScope(FeatureScopeOverrideRow row, FeatureScope scope)
    {
        return row.ScopeType switch
        {
            "global" => true,
            "factory" => !string.IsNullOrEmpty(scope.Factory) && row.ScopeId == scope.Factory,
            "workshop" => !string.IsNullOrEmpty(scope.Workshop) && row.ScopeId == scope.Workshop,
            "line" => !string.IsNullOrEmpty(scope.Line) && row.ScopeId == scope.Line,
            _ => false,   // 未知层级不参与匹配（防御脏数据，与原 SQL 行为一致：都不匹配则无覆盖）
        };
    }

    private static bool IsEffective(FeatureScopeOverrideRow row, DateTime utcNow) =>
        (row.EffectiveFrom is null || row.EffectiveFrom <= utcNow)
        && (row.EffectiveTo is null || row.EffectiveTo > utcNow);

    private static int ScopeRank(string scopeType) => scopeType switch
    {
        "line" => 3,
        "workshop" => 2,
        "factory" => 1,
        _ => 0,
    };

    private static int CompareEffectiveFrom(DateTime? left, DateTime? right)
    {
        // NULLS LAST 语义：null 排在所有非空之后
        if (left is null)
        {
            return right is null ? 0 : -1;
        }

        if (right is null)
        {
            return 1;
        }

        return DateTime.Compare(left.Value, right.Value);
    }
}
