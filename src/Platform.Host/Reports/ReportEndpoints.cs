// 02 §8.2 报表查询/导出端点（M3-2）：
//   防线 1 功能权限：X-User 权限码检查（M1 会话口径，JWT 随完整鉴权补齐）
//   防线 2 编译器：白名单 + 参数化 + /*SCOPE*/ 范围注入
//   防线 3 执行层：只读超时 + 行数上限 + report_run 审计留痕
//   数据范围（scopeIds）：按 X-Operator 实时读 sys_user_scope 计算（9.2）——all 枚举
//   全量工厂、factory 绑定 id。编译器契约（/*SCOPE*/ 必须存在）已冻结，只换平台侧来源。

using System.Text.Json;
using Platform.Core.Security;
using Platform.Host.Auth;
using Platform.Core.Reports;

namespace Platform.Host.Reports;

public static class ReportEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapReportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/reports");

        // 防线 1（文件头注释一直宣称但原实现缺失）：报表功能权限。
        // def.PermCode 原来从未被使用——任何用户无需报表权限码即可查询/导出
        async Task<IResult?> CheckPermAsync(string reportCode, ReportDef def,
            IUserScopeStore scopeStore, HttpContext context, CancellationToken cancellationToken)
        {
            var operatorId = OperatorContext.ParseOperator(context);
            if (operatorId is null)
            {
                return Results.Json(new { error = "X-Operator header required" }, statusCode: 401);
            }

            var permissions = await scopeStore.GetPermissionsAsync(operatorId.Value, cancellationToken);
            if (!permissions.Contains(def.PermCode, StringComparer.Ordinal))
            {
                return Results.Json(new { error = $"missing permission: {def.PermCode}" }, statusCode: 403);
            }

            return null;
        }

        // 报表清单（页面下拉）
        group.MapGet("/", async (ReportStore store, CancellationToken cancellationToken) =>
        {
            var reports = await store.ListAsync(cancellationToken);
            return Results.Ok(reports.Select(report => new { code = report.Code, name = report.Name }));
        });

        // 页面查询：编译（拒绝即 400 带原因）→ 执行 → 留痕 → 返回行数据
        // logger 类型参数用 marker 类（端点类本身是 static，不能作 ILogger<T> 的类型参数）
        group.MapPost("/{reportCode}/query", async (string reportCode, QueryRequest request,
            ReportStore store, IUserScopeStore scopeStore, ILogger<ReportLog> logger,
            HttpContext context, CancellationToken cancellationToken) =>
        {
            // 身份 401 先于报表解析 404：否则匿名可借 404/401 差异枚举合法报表码
            var operatorId = OperatorContext.ParseOperator(context);
            if (operatorId is null)
            {
                return Results.Json(new { error = "X-Operator header required" }, statusCode: 401);
            }

            var def = await store.GetEnabledAsync(reportCode, cancellationToken);
            if (def is null)
            {
                return Results.NotFound(new { error = $"report not found or disabled: {reportCode}" });
            }

            if (await CheckPermAsync(reportCode, def, scopeStore, context, cancellationToken) is { } denied)
            {
                return denied;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var allowedTables = await store.GetAllowedTablesAsync(cancellationToken);
                var scopeIds = operatorId is { } id
                    ? await scopeStore.GetVisibleFactoryIdsAsync(id, cancellationToken)
                    : [];
                var compiled = SqlTemplateCompiler.Compile(
                    def.SqlTemplate, def.Params, request.Params, scopeIds, allowedTables, def.MaxRows);
                var result = await store.ExecuteAsync(compiled, def.MaxRows, cancellationToken);

                await store.WriteRunAsync(reportCode, JsonSerializer.Serialize(request.Params, JsonOptions),
                    userId: operatorId, result.RowCount, result.ElapsedMs, success: true, error: null, cancellationToken);
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
                    userId: operatorId, 0, sw.ElapsedMilliseconds, success: false, ex.Message, cancellationToken);
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // xlsx 导出：同一条编译链路（权限/白名单/范围完全一致，杜绝「导出比查询宽」的旁路）
        group.MapPost("/{reportCode}/export", async (string reportCode, QueryRequest request,
            ReportStore store, IUserScopeStore scopeStore, ILogger<ReportLog> logger,
            HttpContext context, CancellationToken cancellationToken) =>
        {
            // 同 query：身份检查前置，防报表码枚举
            var operatorId = OperatorContext.ParseOperator(context);
            if (operatorId is null)
            {
                return Results.Json(new { error = "X-Operator header required" }, statusCode: 401);
            }

            var def = await store.GetEnabledAsync(reportCode, cancellationToken);
            if (def is null)
            {
                return Results.NotFound(new { error = $"report not found or disabled: {reportCode}" });
            }

            if (await CheckPermAsync(reportCode, def, scopeStore, context, cancellationToken) is { } denied)
            {
                return denied;
            }

            try
            {
                var allowedTables = await store.GetAllowedTablesAsync(cancellationToken);
                var scopeIds = operatorId is { } id
                    ? await scopeStore.GetVisibleFactoryIdsAsync(id, cancellationToken)
                    : [];
                var compiled = SqlTemplateCompiler.Compile(
                    def.SqlTemplate, def.Params, request.Params, scopeIds, allowedTables, def.MaxRows);
                var result = await store.ExecuteAsync(compiled, def.MaxRows, cancellationToken);

                // 列定义反序列化（导出契约：field→title 映射来自 report_def.columns）
                var columns = JsonSerializer.Deserialize<List<ReportColumnDef>>(def.ColumnsJson, JsonOptions) ?? [];

                var stream = new MemoryStream();
                XlsxExporter.Export(stream, columns, result.Rows);
                stream.Position = 0;

                await store.WriteRunAsync(reportCode, JsonSerializer.Serialize(request.Params, JsonOptions),
                    userId: operatorId, result.RowCount, result.ElapsedMs, success: true, error: null, cancellationToken);
                logger.LogInformation("report {Code} exported rows={Rows} bytes={Bytes}",
                    reportCode, result.RowCount, stream.Length);

                return Results.File(stream,
                    contentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    fileDownloadName: $"{reportCode}_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
            }
            catch (ReportCompileException ex)
            {
                // 编译拒绝与查询通道同一留痕口径：导出旁路的拒绝不留痕 = 安全信号丢失
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await store.WriteRunAsync(reportCode, JsonSerializer.Serialize(request.Params, JsonOptions),
                    userId: OperatorContext.ParseOperator(context), 0, sw.ElapsedMilliseconds,
                    success: false, ex.Message, cancellationToken);
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

public sealed record QueryRequest(IReadOnlyDictionary<string, object?> Params);

/// <summary>日志类别 marker（static 类不能作 ILogger&lt;T&gt; 的类型参数）。</summary>
public sealed class ReportLog;
