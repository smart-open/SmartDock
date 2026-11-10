using System.Net.Http;
using System.Net.Http.Json;

namespace Platform.Client.Services;

public sealed record DeviceSummaryDto(
    long Id, string DeviceCode, string Name, string DriverId, int PollMs, bool Enabled, long PointCount);

/// <summary>设备台账只读客户端（M1-9 设备页；管理操作随 M3）。</summary>
public sealed class DeviceApiClient(string baseAddress)
{
    private readonly HttpClient _httpClient = HttpClientHolder.Get(baseAddress, TimeSpan.FromSeconds(5));

    public async Task<IReadOnlyList<DeviceSummaryDto>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = await _httpClient.GetFromJsonAsync<DeviceSummaryDto[]>("/api/devices", cancellationToken);
        return devices ?? [];
    }
}
