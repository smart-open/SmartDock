using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.IO;

namespace Platform.Client.Services;

public sealed record ReportDefDto(string Code, string Name);

public sealed record ReportQueryResult(int RowCount, bool Truncated, long ElapsedMs, JsonElement Rows);

/// <summary>报表客户端（8.2）：清单/查询/导出走同一条编译链路，导出结果落临时文件交 Shell 打开。</summary>
public sealed class ReportApiClient(string baseAddress)
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(baseAddress),
        // 报表执行上限 30s（8.2 服务端 CommandTimeout 同口径），比其他 API 宽
        Timeout = TimeSpan.FromSeconds(35),
    };

    public async Task<IReadOnlyList<ReportDefDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var reports = await _httpClient.GetFromJsonAsync<ReportDefDto[]>("/api/reports/", cancellationToken);
        return reports ?? [];
    }

    public async Task<ReportQueryResult> QueryAsync(string code, Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync($"/api/reports/{code}/query",
            new { @params = parameters }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ReportQueryResult>(cancellationToken);
        return result ?? new ReportQueryResult(0, false, 0, JsonSerializer.SerializeToElement(Array.Empty<object>()));
    }

    /// <summary>导出 xlsx 到临时文件并返回路径（服务端同编译链路，8.2 防线一致）。</summary>
    public async Task<string> ExportAsync(string code, Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync($"/api/reports/{code}/export",
            new { @params = parameters }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var path = Path.Combine(Path.GetTempPath(), $"SmartDock-{code}-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx");
        await using var fileStream = File.Create(path);
        await response.Content.CopyToAsync(fileStream, cancellationToken);
        return path;
    }
}
