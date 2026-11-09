// 02 §8.2 报表查询/导出端点（M3-2）：
//   防线 1 功能权限：X-User 权限码检查（M1 会话口径，JWT 随完整鉴权补齐）
//   防线 2 编译器：白名单 + 参数化 + /*SCOPE*/ 范围注入
//   防线 3 执行层：只读超时 + 行数上限 + report_run 审计留痕
//   数据范围（scopeIds）：M3 阶段用户数据范围尚未建（RBAC scope 扩展），先按「全部可见」
//   传全量 plant 列表；范围字段收窄属后续任务，编译器契约（/*SCOPE*/ 必须存在）已冻结。

using System.Text.Json;
using Platform.Core.Reports;

namespace Platform.Host.Reports;

public static class ReportEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapReportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/reports");

        // 报表清单（页面下拉）
        group.MapGet("/", async (ReportStore store, CancellationToken cancellationToken) =>
        {
            var reports = await store.ListAsync(cancellationToken);
            return Results.Ok(reports.Select(report => new { code = report.Code, name = report.Name }));
        });

        // 页面查询：编译（拒绝即 400 带原因）→ 执行 → 留痕 → 返回行数据
        // logger 类型参数用 marker 类（端点类本身是 static，不能作 ILogger<T> 的类型参数）
        group.MapPost("/{reportCode}/query", async (string reportCode, QueryRequest request,
            ReportStore store, ILogger<ReportLog> logger, CancellationToken cancellationToken) =>
        {
            var def = await store.GetEnabledAsync(reportCode, cancellationToken);
            if (def is null)
            {
                return Results.NotFound(new { error = $"report not found or disabled: {reportCode}" });
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var allowedTables = await store.GetAllowedTablesAsync(cancellationToken);
                var compiled = SqlTemplateCompiler.Compile(
                    def.SqlTemplate, def.Params, request.Params, [1], allowedTables, def.MaxRows);
                var result = await store.ExecuteAsync(compiled, def.MaxRows, cancellationToken);

                await store.WriteRunAsync(reportCode, JsonSerializer.Serialize(request.Params, JsonOptions),
                    userId: null, result.RowCount, result.ElapsedMs, success: true, error: null, cancellationToken);
                logger.LogInformation("report {Code} queried rows={Rows} elapsed={Ms}ms",
                    reportCode, result.RowCount, result.ElapsedMs);

                return Results.Ok(new
                {
                    rowCount = result.RowCount,
                    truncated = result.Truncated,
                    elapsedMs = result.ElapsedMs,
                    rows = result.Rows
                });
            }
            catch (ReportCompileException ex)
            {
                // 编译拒绝也留痕：反复触发拒绝的账号是安全信号（8.2 违反后果=拒绝执行）
                await store.WriteRunAsync(reportCode, JsonSerializer.Serialize(request.Params, JsonOptions),
                    userId: null, 0, sw.ElapsedMilliseconds, success: false, ex.Message, cancellationToken);
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // xlsx 导出：同一条编译链路（权限/白名单/范围完全一致，杜绝「导出比查询宽」的旁路）
        group.MapPost("/{reportCode}/export", async (string reportCode, QueryRequest request,
            ReportStore store, ILogger<ReportLog> logger, CancellationToken cancellationToken) =>
        {
            var def = await store.GetEnabledAsync(reportCode, cancellationToken);
            if (def is null)
            {
                return Results.NotFound(new { error = $"report not found or disabled: {reportCode}" });
            }

            try
            {
                var allowedTables = await store.GetAllowedTablesAsync(cancellationToken);
                var compiled = SqlTemplateCompiler.Compile(
                    def.SqlTemplate, def.Params, request.Params, [1], allowedTables, def.MaxRows);
                var result = await store.ExecuteAsync(compiled, def.MaxRows, cancellationToken);

                // 列定义反序列化（导出契约：field→title 映射来自 report_def.columns）
                var columns = JsonSerializer.Deserialize<List<ReportColumnDef>>(def.ColumnsJson, JsonOptions) ?? [];

                var stream = new MemoryStream();
                XlsxExporter.Export(stream, columns, result.Rows);
                stream.Position = 0;

                await store.WriteRunAsync(reportCode, JsonSerializer.Serialize(request.Params, JsonOptions),
                    userId: null, result.RowCount, result.ElapsedMs, success: true, error: null, cancellationToken);
                logger.LogInformation("report {Code} exported rows={Rows} bytes={Bytes}",
                    reportCode, result.RowCount, stream.Length);

                return Results.File(stream,
                    contentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    fileDownloadName: $"{reportCode}_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
            }
            catch (ReportCompileException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

public sealed record QueryRequest(IReadOnlyDictionary<string, object?> Params);

/// <summary>日志类别 marker（static 类不能作 ILogger&lt;T&gt; 的类型参数）。</summary>
public sealed class ReportLog;
