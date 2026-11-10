using System.Net.Http;
using System.Net.Http.Json;

namespace Platform.Client.Services;

/// <summary>授权状态 DTO（13.6 /api/license 只读端点；登录卡脚注的数据面）。</summary>
public sealed record LicenseInfoDto(
    bool Loaded, string? LicenseId, string? Customer, string? Edition,
    DateTime? ExpiresAt, int? DaysRemaining, string Phase);

/// <summary>授权查询客户端：失败静默返回 null（脚注允许无授权信息，不挡登录流程）。</summary>
public sealed class LicenseApiClient(string baseAddress)
{
    private readonly HttpClient _httpClient = HttpClientHolder.Get(baseAddress, TimeSpan.FromSeconds(5));

    public async Task<LicenseInfoDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<LicenseInfoDto>("/api/license", cancellationToken);
        }
        catch
        {
            return null;   // 脚注降级为版本号：授权面故障不许挡住操作员上班
        }
    }
}
