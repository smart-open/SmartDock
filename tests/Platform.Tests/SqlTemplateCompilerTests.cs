// 02 §8.2 编译器规则逐条锁定：/*SCOPE*/ 必须存在、系统表拒绝、表白名单、
// 参数声明完备、强类型转换、limit 上限钳制、scope 平台覆盖。

using Platform.Core.Reports;

namespace Platform.Tests;

public class SqlTemplateCompilerTests
{
    private static readonly IReadOnlySet<string> Whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "work_order", "production_report"
    };

    private static readonly IReadOnlyList<ReportParamDef> ParamDefs =
    [
        // Default 用 JsonElement（report_def.params jsonb 里数字/字符串默认值都可能）
        new("from", "datetime", Required: true, Default: null),
        new("to", "datetime", Required: true, Default: null),
        new("limit", "int", Required: false, Default: System.Text.Json.JsonSerializer.SerializeToElement(100)),
    ];

    private const string ValidTemplate = """
        SELECT o.order_no FROM production_report r
        JOIN work_order o ON o.id = r.order_id
        WHERE r.reported_at >= @from AND r.reported_at < @to
          /*SCOPE*/ AND o.plant_id = ANY(@scopeIds)
        LIMIT @limit
        """;

    private static CompiledReport Compile(string template, Dictionary<string, object?>? userParams = null) =>
        SqlTemplateCompiler.Compile(
            template, ParamDefs, userParams ?? new Dictionary<string, object?>(),
            [7, 9], Whitelist, maxRows: 1000);

    [Fact]
    public void Compile_valid_template_removes_scope_placeholder_and_binds_params()
    {
        var compiled = Compile(ValidTemplate, new() { ["from"] = "2026-11-01", ["to"] = "2026-11-09" });

        // 占位符必须被移除（不能把 /*SCOPE*/ 原样发到 PG 当注释执行——那等于没有范围过滤锚点）
        Assert.DoesNotContain("/*SCOPE*/", compiled.Sql);
        Assert.Equal(new DateTime(2026, 11, 1), compiled.Parameters["from"]);
        // scope 由平台注入，与用户参数无关
        Assert.Equal(new long[] { 7, 9 }, compiled.Parameters["scopeIds"]);
        // 未传 limit → 用默认 100
        Assert.Equal(100, compiled.Parameters["limit"]);
        Assert.Equal(100, compiled.EffectiveLimit);
    }

    [Fact]
    public void Compile_missing_scope_placeholder_is_rejected()
    {
        var template = "SELECT order_no FROM work_order WHERE order_no = @from";
        var ex = Assert.Throws<ReportCompileException>(() => Compile(template));
        Assert.Contains("SCOPE", ex.Message);
    }

    [Theory]
    [InlineData("SELECT table_name FROM pg_catalog.pg_tables /*SCOPE*/")]
    [InlineData("SELECT column_name FROM information_schema.columns /*SCOPE*/")]
    public void Compile_system_schemas_are_rejected(string template)
    {
        var ex = Assert.Throws<ReportCompileException>(() => Compile(template));
        Assert.Contains("forbidden schema", ex.Message);
    }

    [Theory]
    [InlineData("SELECT secret FROM sys_user /*SCOPE*/")]                 // 台账外的业务表
    [InlineData("SELECT content FROM point_history /*SCOPE*/")]          // 未加白的大表
    public void Compile_tables_outside_whitelist_are_rejected(string template)
    {
        var ex = Assert.Throws<ReportCompileException>(() => Compile(template));
        Assert.Contains("not in whitelist", ex.Message);
    }

    [Fact]
    public void Compile_undeclared_param_is_rejected()
    {
        // 模板引用了未声明的 @shadows——无法强类型校验即拒绝（8.2 参数化规则）
        var template = ValidTemplate.Replace("@to", "@to AND o.order_no = @shadows");
        var ex = Assert.Throws<ReportCompileException>(() => Compile(template));
        Assert.Contains("undeclared param", ex.Message);
    }

    [Fact]
    public void Compile_missing_required_param_is_rejected()
    {
        var ex = Assert.Throws<ReportCompileException>(
            () => Compile(ValidTemplate, new() { ["from"] = "2026-11-01" }));   // 缺 to
        Assert.Contains("missing required param: to", ex.Message);
    }

    [Fact]
    public void Compile_bad_typed_param_is_rejected()
    {
        // from 是 datetime 定义，喂非法日期 → 编译期拒绝（而非运行期 PG 报模糊错误）
        var userParams = new Dictionary<string, object?> { ["from"] = "not-a-date", ["to"] = "2026-11-09" };
        var ex = Assert.Throws<ReportCompileException>(() => Compile(ValidTemplate, userParams));
        Assert.Contains("from", ex.Message);
    }

    [Fact]
    public void Compile_limit_as_string_is_ignored_and_platform_value_used()
    {
        // limit 是平台参数：用户传字符串（非 int）不参与钳制，回退到参数定义默认值 100
        // （字符串值不会被注入 SQL——它从未进入参数字典，更不可能携带注入载荷）
        var compiled = Compile(ValidTemplate, new()
        {
            ["from"] = "2026-11-01", ["to"] = "2026-11-09", ["limit"] = "999; DROP TABLE x"
        });
        Assert.Equal(100, compiled.Parameters["limit"]);    // 定义默认值
    }

    [Fact]
    public void Compile_user_limit_is_clamped_to_max_rows()
    {
        // 用户想拉 100 万行 → 被钳到 maxRows=1000（防单次拖库）
        var compiled = Compile(ValidTemplate, new()
        {
            ["from"] = "2026-11-01", ["to"] = "2026-11-09", ["limit"] = 1_000_000
        });
        Assert.Equal(1000, compiled.Parameters["limit"]);
    }

    [Fact]
    public void Compile_scope_ids_are_platform_controlled()
    {
        // 用户伪造 scopeIds 参数试图扩大可见范围 → 被平台值覆盖（规则 4 的存在意义）
        var compiled = Compile(ValidTemplate, new()
        {
            ["from"] = "2026-11-01",
            ["to"] = "2026-11-09",
            ["scopeIds"] = new long[] { 999 },   // 用户伪造
        });
        Assert.Equal(new long[] { 7, 9 }, compiled.Parameters["scopeIds"]);
    }

    [Fact]
    public void Export_writes_headers_and_rows_with_defined_columns()
    {
        // 导出契约：列标题来自定义（中文），字段按列定义选取，缺字段 = 空白单元格
        var columns = new List<ReportColumnDef>
        {
            new("order_no", "工单号"),
            new("good_qty", "良品数"),
            new("missing_field", "缺失列"),
        };
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["order_no"] = "WO-1", ["good_qty"] = 42 },
            new Dictionary<string, object?> { ["order_no"] = "WO-2", ["good_qty"] = null },
        };

        using var stream = new MemoryStream();
        XlsxExporter.Export(stream, columns, rows);
        stream.Position = 0;

        using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
        var sheet = workbook.Worksheet(1);
        Assert.Equal("工单号", sheet.Cell(1, 1).GetString());
        Assert.Equal("WO-1", sheet.Cell(2, 1).GetString());
        Assert.Equal(42m, sheet.Cell(2, 2).GetValue<decimal>());
        // null 单元格为空白而非抛错
        Assert.True(sheet.Cell(3, 2).IsEmpty());
        // 未定义字段（missing_field）= 空白
        Assert.True(sheet.Cell(2, 3).IsEmpty());
    }
}
