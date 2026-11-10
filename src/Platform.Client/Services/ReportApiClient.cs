using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.IO;

namespace Platform.Client.Services;

public sealed record ReportDefDto(string Code, string Name);

public sealed record ReportQueryResult(int RowCount, bool Truncated, long ElapsedMs, JsonElement Rows);

/// <summary>报表客户端（8.2）：清单/查询/导出走同一条编译链路，导出结果落临时文件交 Shell 打开。</summary>
public sealed class ReportApiClient(string baseAddress, long operatorId)
{
    private readonly HttpClient _httpClient =
        HttpClientHolder.Get(baseAddress, TimeSpan.FromSeconds(35));   // 30s 同服务端 CommandTimeout 口径

    // code 来自服务端清单（半可信）：白名单校验防 URL 路径语义变化与非法文件名
    private static readonly System.Text.RegularExpressions.Regex CodePattern =
        new("^[A-Za-z0-9_-]{1,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<IReadOnlyList<ReportDefDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var reports = await _httpClient.GetFromJsonAsync<ReportDefDto[]>("/api/reports/", cancellationToken);
        return reports ?? [];
    }

    public async Task<ReportQueryResult> QueryAsync(string code, Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCodeInvalid(code);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/reports/{code}/query")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { @params = parameters }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Operator", operatorId.ToString());
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ReportQueryResult>(cancellationToken);
        return result ?? new ReportQueryResult(0, false, 0, JsonSerializer.SerializeToElement(Array.Empty<object>()));
    }

    /// <summary>导出 xlsx 到临时文件并返回路径（服务端同编译链路，8.2 防线一致）。</summary>
    public async Task<string> ExportAsync(string code, Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCodeInvalid(code);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/reports/{code}/export")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { @params = parameters }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Operator", operatorId.ToString());
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var path = Path.Combine(Path.GetTempPath(), $"SmartDock-{code}-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx");
        await using var fileStream = File.Create(path);
        await response.Content.CopyToAsync(fileStream, cancellationToken);
        return path;
    }

    private static void ThrowIfCodeInvalid(string code)
    {
        if (!CodePattern.IsMatch(code))
        {
            throw new ArgumentException($"invalid report code: {code}");
        }
    }
}
