// 02 §8.3 导出：10 万行内同步流式写 xlsx（ClosedXML）。列标题来自 report_def.columns
//（页面与导出共用同一口径，8.1 列模型），行数据来自 ReportRunResult。

using ClosedXML.Excel;

namespace Platform.Core.Reports;

/// <summary>列定义（report_def.columns jsonb：字段、标题）。</summary>
public sealed record ReportColumnDef(string Field, string Title);

public static class XlsxExporter
{
    /// <summary>把查询结果写成 xlsx 到目标流（不落临时文件，内存直出）。</summary>
    public static void Export(
        Stream output,
        IReadOnlyList<ReportColumnDef> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("report");

        // 表头：用定义的标题而非裸字段名（中文列名是报表的基本交付质量）
        for (var index = 0; index < columns.Count; index++)
        {
            var cell = sheet.Cell(1, index + 1);
            cell.Value = columns[index].Title;
            cell.Style.Font.Bold = true;
        }

        // 数据行：按列定义取字段，未出现的字段不导出（列定义即导出契约）
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var value = row.TryGetValue(columns[columnIndex].Field, out var raw) ? raw : null;
                sheet.Cell(rowIndex + 2, columnIndex + 1).Value = ToCellValue(value);
            }
        }

        workbook.SaveAs(output);
    }

    // ClosedXML 的 XLCellValue 不收 DBNull/null，需显式转空白单元格
    private static XLCellValue ToCellValue(object? value) => value switch
    {
        null or DBNull => Blank.Value,
        bool flag => flag,
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToDecimal(value),
        DateTime dt => dt,
        string text => text,
        _ => value.ToString() ?? string.Empty
    };
}
