using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Platform.Client.Services;

/// <summary>认证 API 客户端（登录态仅存客户端内存；服务端 WriteGuard 才是写操作安全边界）。</summary>
public sealed record LoginOutcome(long UserId, string UserName, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

public sealed class AuthApiClient(string baseAddress)
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(baseAddress),
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>登录成功返回会话；失败返回 null（401/400 统一口径，客户端不区分原因防枚举展示）。</summary>
    public async Task<LoginOutcome?> LoginAsync(string userCode, string password, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync(
            "/api/auth/login",
            new StringContent(JsonSerializer.Serialize(new { userCode, password }), Encoding.UTF8, "application/json"),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new LoginOutcome(
            doc.GetProperty("userId").GetInt64(),
            doc.GetProperty("userName").GetString() ?? userCode,
            doc.GetProperty("roles").EnumerateArray().Select(v => v.GetString() ?? "").ToArray(),
            doc.GetProperty("permissions").EnumerateArray().Select(v => v.GetString() ?? "").ToArray());
    }
}
