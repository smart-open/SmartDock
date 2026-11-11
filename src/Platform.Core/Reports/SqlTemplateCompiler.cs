// 02 §8.2 安全 SQL 三道防线之「编译器」：白名单 + 参数化 + 范围注入。
// 报表模板不是「写 SQL 查数据」而是「受控查询」——本类是模板到可执行 SQL 的唯一通道，
// 所有越权风险（任意表、系统表、未声明参数、缺范围过滤）都在编译期拒绝，而不是运行期祈祷。
// 设计为纯函数（不触库）：表白名单由 Store 读出后传入，便于单测覆盖全部注入面。

using System.Text.RegularExpressions;

namespace Platform.Core.Reports;

/// <summary>报表参数定义（来自 report_def.params，jsonb）。Default 为原始 JSON 元素（数字/字符串都可能）。</summary>
public sealed record ReportParamDef(string Name, string Type, bool Required, System.Text.Json.JsonElement? Default)
{
    /// <summary>默认值的字符串形态（供 int.TryParse / ConvertParam 使用；null = 无默认）。</summary>
    public string? DefaultText => Default?.ToString();
}

/// <summary>编译产物：可执行 SQL + 参数清单（用户参数 + 平台注入的 scopeIds/limit）。</summary>
public sealed record CompiledReport(
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    int EffectiveLimit);

/// <summary>编译失败（8.2 各规则的违反后果均为「拒绝执行」）。</summary>
public sealed class ReportCompileException(string reason) : Exception(reason);

public static class SqlTemplateCompiler
{
    // /*SCOPE*/ 占位符：编译期删除，谓词本体由模板作者写在占位符之后。
    // 缺占位符 = 模板没声明范围过滤点 = 一票否决（8.2 范围注入）。
    private const string ScopePlaceholder = "/*SCOPE*/";

    // 8.2 禁止系统表：防元数据嗅探（表结构/列名枚举是注入攻击的第一步）。
    private static readonly string[] ForbiddenSchemas = ["pg_catalog", "information_schema"];

    // 提取 FROM/JOIN 后的表名做白名单核对。正则只做粗筛，
    // 漏报（如子查询别名写法）由白名单兜底——不在清单内的表一律拒绝。
    private static readonly Regex TableRefRegex =
        new(@"\b(?:FROM|JOIN)\s+([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 模板里引用的 @param。用户输入只作为 Npgsql 参数传递，绝不拼进 SQL 文本。
    private static readonly Regex ParamRegex =
        new(@"@([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled);

    // 注释剥离：FROM /*x*/ secret_table / FROM 换行 -- 注释 再接表名可让表名提取漏报，
    // 白名单承诺被刻意绕过（8.2 防线 2 有缝）。扫描前先剥掉 --… 与 /*…*/ 注释
    private static readonly Regex LineCommentRegex = new(@"--[^\r\n]*", RegexOptions.Compiled);
    private static readonly Regex BlockCommentRegex = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// 编译报表模板。
    /// userParams：用户传入参数；scopeIds：平台计算的可见范围（用户不可传，覆盖同名模板参数）。
    /// </summary>
    public static CompiledReport Compile(
        string sqlTemplate,
        IReadOnlyList<ReportParamDef> paramDefs,
        IReadOnlyDictionary<string, object?> userParams,
        IReadOnlyList<long> scopeIds,
        IReadOnlySet<string> allowedTables,
        int maxRows)
    {
        // 规则 0：模板必须声明范围过滤点
        if (!sqlTemplate.Contains(ScopePlaceholder, StringComparison.Ordinal))
        {
            throw new ReportCompileException("template missing /*SCOPE*/ placeholder");
        }

        // 先移除占位符：后续 schema/表名检查都在「干净文本」上做，
        // 避免注释词（SCOPE）混入表名提取（/*SCOPE*/ 是给编译器的标记，不属于 SQL 语义）
        var compiledSql = sqlTemplate.Replace(ScopePlaceholder, string.Empty, StringComparison.Ordinal);

        // 规则 1：系统表/Schema 禁用
        foreach (var schema in ForbiddenSchemas)
        {
            if (compiledSql.Contains(schema, StringComparison.OrdinalIgnoreCase))
            {
                throw new ReportCompileException($"forbidden schema: {schema}");
            }
        }

        // 规则 2：表白名单——所有 FROM/JOIN 引用的表都必须在清单内
        // 先抹掉 @param 引用：参数名（如 @from）含关键字字样会被 TableRefRegex 误捕（@from AND → "FROM AND"）
        // 再剥注释（先块后行）：注释里的 FROM/表名不参与白名单核对，防止借注释夹带绕过
        var textForTableScan = ParamRegex.Replace(compiledSql, "p");
        textForTableScan = BlockCommentRegex.Replace(textForTableScan, " ");
        textForTableScan = LineCommentRegex.Replace(textForTableScan, " ");
        var rejectedTables = TableRefRegex.Matches(textForTableScan)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(table => !allowedTables.Contains(table))
            .ToList();
        if (rejectedTables.Count > 0)
        {
            throw new ReportCompileException($"table(s) not in whitelist: {string.Join(",", rejectedTables)}");
        }

        // 规则 3：参数声明完备——模板引用的每个 @param（除平台注入的 scopeIds）必须有定义，
        // 未声明参数无法做强类型校验，等于开了注入后门。
        var templateParams = ParamRegex.Matches(compiledSql)
            .Select(match => match.Groups[1].Value)
            .Where(name => name != "scopeIds")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var declaredNames = paramDefs.Select(def => def.Name).ToHashSet(StringComparer.Ordinal);
        var undeclared = templateParams.Where(name => !declaredNames.Contains(name)).ToList();
        if (undeclared.Count > 0)
        {
            throw new ReportCompileException($"template references undeclared param(s): {string.Join(",", undeclared)}");
        }

        // 规则 5：行数上限——limit 恒由平台设定，取 min(用户 limit, max_rows)，
        // 无用户 limit 时用参数定义的默认值（仍受 max_rows 钳制），防止单次拖库
        // limit 来自 HTTP JSON → System.Text.Json 反序列化成 JsonElement（不是 int），
        // 原实现 `rawLimit is int` 恒 false → 用户 limit 被静默忽略
        int? userLimit = null;
        if (userParams.TryGetValue("limit", out var rawLimit))
        {
            userLimit = rawLimit switch
            {
                int parsedLimit => parsedLimit,
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } element
                    when element.TryGetInt32(out var jsonLimit) => jsonLimit,
                _ => null,
            };
        }

        var defaultLimit = int.TryParse(paramDefs.FirstOrDefault(def => def.Name == "limit")?.DefaultText, out var parsedDefault)
            ? parsedDefault
            : (int?)null;
        // DefaultText：record 主构造属性是 JsonElement?，数字/字符串默认值统一走字符串形态解析
        var effectiveLimit = Math.Clamp(userLimit ?? defaultLimit ?? maxRows, 1, maxRows);

        // 组装最终参数：用户参数按定义强类型转换；平台参数最后覆盖（防同名伪造）
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var def in paramDefs)
        {
            if (def.Name is "scopeIds" or "limit")
            {
                continue;   // 平台参数不走用户定义通道
            }

            // TryGetValue 出参可能为 null（字典值可空），ConvertParam 内部对 null 显式报错
            if (userParams.TryGetValue(def.Name, out var value) && value is not null)
            {
                parameters[def.Name] = ConvertParam(def, value);
            }
            else if (def.Required)
            {
                throw new ReportCompileException($"missing required param: {def.Name}");
            }
            else if (def.Default is not null)
            {
                parameters[def.Name] = ConvertParam(def, def.DefaultText!);
            }
        }

        parameters["scopeIds"] = scopeIds;
        if (templateParams.Contains("limit", StringComparer.Ordinal))
        {
            parameters["limit"] = effectiveLimit;
        }

        return new CompiledReport(compiledSql, parameters, effectiveLimit);
    }

    // 参数强类型转换（8.2「@from/@to 来自参数，强类型校验」）：
    // 类型不符编译期报错，而不是把字符串原样丢进库等它报模糊错误。
    private static object ConvertParam(ReportParamDef def, object raw)
    {
        var text = raw.ToString() ?? throw new ReportCompileException($"param {def.Name} is null");
        try
        {
            return def.Type switch
            {
                // 文化无关 + 统一 UTC 口径：随服务器区域解析对 03/04/2026 语义相反，
                // Kind=Unspecified 落 timestamptz 的时区解释取决于会话 TimeZone
                "datetime" => DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal),
                "int" => int.Parse(text),
                "long" => long.Parse(text),
                "string" => text,
                _ => throw new ReportCompileException($"unknown param type: {def.Type}")
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            // OverflowException（99999999999）/文化相关 ArgumentException 与 FormatException
            // 同为「参数非法」：统一收敛为编译拒绝（400），不能穿透成裸 500
            throw new ReportCompileException($"param {def.Name} expects {def.Type}, got '{text}'");
        }
    }
}
