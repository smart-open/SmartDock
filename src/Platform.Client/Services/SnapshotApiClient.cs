using System.Net.Http;
using System.Net.Http.Json;
using Platform.Abstractions;

namespace Platform.Client.Services;

/// <summary>快照汇总 DTO（/api/snapshot/summary）：总览 KPI 与大屏可用率的数据面。</summary>
public sealed record SnapshotSummaryDto(int Total, int Good, int Bad, int Uncertain, int Stale);

/// <summary>快照 API 客户端（B1；客户端不直连数据库，2.3）。</summary>
public sealed class SnapshotApiClient(string baseAddress)
{
    private readonly HttpClient _httpClient =
        HttpClientHolder.Get(baseAddress, TimeSpan.FromSeconds(5));   // 5s 超时：API 卡死时 UI 能感知而不是无限等

    public async Task<IReadOnlyList<ReadValue>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _httpClient.GetFromJsonAsync<ReadValue[]>("/api/snapshot", cancellationToken);
        return snapshot ?? [];
    }

    /// <summary>快照质量分布汇总：断线时由调用方降级（总览 KPI 冻结 + 标红），此处不吞异常。</summary>
    public async Task<SnapshotSummaryDto?> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<SnapshotSummaryDto>("/api/snapshot/summary", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;   // 网络抖动返回 null：调用方按「本轮无数据」处理，不中断轮询
        }
    }
}
