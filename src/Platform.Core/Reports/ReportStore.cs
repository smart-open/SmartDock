// 02 §8.1 报表模型存储：定义读取（启用态）、表白名单、执行与留痕。
// 放 Platform.Core（而非 Data）是因为它依赖本项目的编译器类型（CompiledReport），
// 且 Core → Abstractions 的引用链上 Npgsql 已可用（Data 同款依赖）；ADR 分层不受影响。

using System.Text.Json;
using Npgsql;

namespace Platform.Core.Reports;

public sealed record ReportDef(
    string ReportCode,
    string ReportName,
    string SqlTemplate,
    IReadOnlyList<ReportParamDef> Params,
    string ColumnsJson,
    string PermCode,
    string ScopeField,
    int MaxRows);

public sealed record ReportRunResult(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int RowCount,
    long ElapsedMs,
    bool Truncated);

public sealed class ReportStore
{
    // report_def.params / columns 的 jsonb 反序列化（8.1：参数 = 名称/类型/必填/默认；列 = 字段/标题）
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;

    public ReportStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>读启用态报表定义（8.2 防线 1 之前的入口：找不到/停用即拒绝）。</summary>
    public async Task<ReportDef?> GetEnabledAsync(string reportCode, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT report_code, report_name, sql_template, params, columns, perm_code, scope_field, max_rows
            FROM report_def
            WHERE report_code = @code AND enabled = true
            """);
        command.Parameters.AddWithValue("code", reportCode);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var paramDefs = JsonSerializer.Deserialize<List<ReportParamDef>>(reader.GetString(3), JsonOptions) ?? [];
        return new ReportDef(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            paramDefs,
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7));
    }

    /// <summary>报表清单（页面下拉用；不暴露 SQL 模板——模板属定义域）。</summary>
    public async Task<IReadOnlyList<(string Code, string Name)>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT report_code, report_name FROM report_def WHERE enabled = true ORDER BY report_code");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<(string, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add((reader.GetString(0), reader.GetString(1)));
        }

        return list;
    }

    /// <summary>表白名单（8.2 编译器规则 2 的数据源）。</summary>
    public async Task<IReadOnlySet<string>> GetAllowedTablesAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("SELECT table_name FROM report_allowed_table");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    /// <summary>
    /// 执行编译后的报表 SQL（8.2 防线 3：超时 30s + 行数探测）。
    /// 读到 maxRows+1 行即判定溢出（探测行丢弃，truncated=true 让客户端提示缩小范围）。
    /// </summary>
    public async Task<ReportRunResult> ExecuteAsync(
        CompiledReport compiled, int maxRows, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await using var command = _dataSource.CreateCommand(compiled.Sql);
        command.CommandTimeout = 30;    // 8.2 超时规则
        foreach (var (name, value) in compiled.Parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var capacity = maxRows + 1;     // 多读一行探测「还有更多」
        while (await reader.ReadAsync(cancellationToken) && rows.Count < capacity)
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var column = 0; column < reader.FieldCount; column++)
            {
                row[reader.GetName(column)] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            }

            rows.Add(row);
        }

        stopwatch.Stop();
        var truncated = rows.Count > maxRows;
        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);  // 探测行不上报
        }

        return new ReportRunResult(rows, rows.Count, stopwatch.ElapsedMilliseconds, truncated);
    }

    /// <summary>执行留痕（8.1 执行记录：审计与排障）。编译拒绝也留痕——失败原因是安全线索。</summary>
    public async Task WriteRunAsync(string reportCode, string? paramsJson, long? userId,
        int rowCount, long elapsedMs, bool success, string? error, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            """
            INSERT INTO report_run (report_code, params, user_id, row_count, elapsed_ms, status, error)
            VALUES (@code, @params::jsonb, @user_id, @rows, @elapsed, @status, @error)
            """);
        command.Parameters.AddWithValue("code", reportCode);
        command.Parameters.AddWithValue("params", paramsJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("user_id", userId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("rows", rowCount);
        command.Parameters.AddWithValue("elapsed", (int)Math.Clamp(elapsedMs, 0, int.MaxValue));
        command.Parameters.AddWithValue("status", (short)(success ? 0 : 1));
        command.Parameters.AddWithValue("error", error ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
