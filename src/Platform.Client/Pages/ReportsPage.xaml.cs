namespace Platform.Client.Pages;

using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using Platform.Client.Services;

/// <summary>
/// 报表查看页（8.2）：固定报表清单 → from/to 必填参数 → 查询/导出。
/// 参数面只暴露时间范围——种子报表 production.summary 的 params 即此口径；
/// 自定义参数面板随报表设计器（P2）跟进。
/// </summary>
public partial class ReportsPage : UserControl
{
    private readonly ReportApiClient _apiClient;
    private bool _busy;   // 查询/导出防重入：35s 超时窗口内连点会叠加请求、后完成者覆盖结果

    public ReportsPage(string baseAddress, long operatorId)
    {
        InitializeComponent();
        _apiClient = new ReportApiClient(baseAddress, operatorId);
        FromDate.SelectedDate = DateTime.Today.AddDays(-7);
        ToDate.SelectedDate = DateTime.Today;
        _ = LoadReportsAsync();
    }

    private async Task LoadReportsAsync()
    {
        try
        {
            var reports = await _apiClient.ListAsync();
            ReportCombo.ItemsSource = reports;
            ReportCombo.DisplayMemberPath = nameof(ReportDefDto.Name);
            if (reports.Count > 0)
            {
                ReportCombo.SelectedIndex = 0;
            }
            ErrorText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"报表清单加载失败：{ex.Message}";
        }
    }

    private void OnReportSelected(object sender, SelectionChangedEventArgs e) { }

    private Dictionary<string, object?> BuildParameters()
    {
        // 必填校验前置到客户端：服务端编译器也会拒绝（S17.3），但少一次往返
        return new Dictionary<string, object?>
        {
            ["from"] = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-7)).ToString("yyyy-MM-dd 00:00:00"),
            ["to"] = (ToDate.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd 23:59:59"),
        };
    }

    private bool Validate(out string error)
    {
        if (ReportCombo.SelectedItem is not ReportDefDto)
        {
            error = "请选择报表";
            return false;
        }
        if (FromDate.SelectedDate is null || ToDate.SelectedDate is null)
        {
            error = "请选择起止日期";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private async void OnQueryClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (!Validate(out var validationError))
        {
            ErrorText.Text = validationError;
            return;
        }

        _busy = true;
        var code = ((ReportDefDto)ReportCombo.SelectedItem).Code;
        try
        {
            StatusText.Text = "查询中…";
            var result = await _apiClient.QueryAsync(code, BuildParameters());
            // ItemsSource 需要 IEnumerable；DataTable 走 DefaultView（且排序/过滤可用）
            ResultGrid.ItemsSource = ToDataTable(result.Rows).DefaultView;
            StatusText.Text = $"{result.RowCount} 行（{result.ElapsedMs}ms）{(result.Truncated ? "【已截断】" : "")}";
            ErrorText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"查询失败：{ex.Message}";
            StatusText.Text = string.Empty;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (!Validate(out var validationError))
        {
            ErrorText.Text = validationError;
            return;
        }

        _busy = true;
        var code = ((ReportDefDto)ReportCombo.SelectedItem).Code;
        try
        {
            StatusText.Text = "导出中…";
            var path = await _apiClient.ExportAsync(code, BuildParameters());
            StatusText.Text = $"已导出：{path}";
            // 打开所在目录比直接打开文件更安全（文件可能被 Excel 锁定）
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            ErrorText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"导出失败：{ex.Message}";
            StatusText.Text = string.Empty;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>动态列（rows 是 JsonElement 数组）：用 DataTable 让 AutoGenerateColumns 生效。</summary>
    private static DataTable ToDataTable(JsonElement rows)
    {
        var table = new DataTable();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in row.EnumerateObject())
            {
                if (!table.Columns.Contains(property.Name))
                {
                    table.Columns.Add(property.Name);
                }
            }

            var dataRow = table.NewRow();
            foreach (var property in row.EnumerateObject())
            {
                dataRow[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.Number => property.Value.GetDecimal(),
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => property.Value.ToString(),
                };
            }
            table.Rows.Add(dataRow);
        }
        return table;
    }
}
