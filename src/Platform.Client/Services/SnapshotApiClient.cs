using System.Net.Http;
using System.Net.Http.Json;
using Platform.Abstractions;

namespace Platform.Client.Services;

/// <summary>快照 API 客户端（B1；客户端不直连数据库，2.3）。</summary>
public sealed class SnapshotApiClient(string baseAddress)
{
    private readonly HttpClient _httpClient = new() { BaseAddress = new Uri(baseAddress) };

    public async Task<IReadOnlyList<ReadValue>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _httpClient.GetFromJsonAsync<ReadValue[]>("/api/snapshot", cancellationToken);
        return snapshot ?? [];
    }
}
