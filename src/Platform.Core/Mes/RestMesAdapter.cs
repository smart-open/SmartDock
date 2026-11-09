// 02 §6.10 契约测试口径的真实协议适配器：HTTP POST 推对方 REST 端点（放 Core 因测试工程需覆盖分流；
// HttpClient 属 BCL，符合 2.1 分层）。
// 分流规则（与 6.10 测试表对应）：
//   2xx          → Ok（响应体作 ack_value）
//   408/429/5xx  → Retryable（退避）
//   其他 4xx     → Retryable（走完 6 次阶梯后 Dead，与 6.10「5xx 退避最终 Dead」一致；快速 Dead 随 6.13 补）
//   超时          → Unknown（转 Suspended 对账——请求可能已到对方，盲目重发会重复入账，6.5）
//   连接失败      → Retryable（请求大概率没到对方，断网补传语义）

using System.Text;
using Microsoft.Extensions.Options;
using Platform.Abstractions;

namespace Platform.Core.Mes;

public sealed class RestMesOptions
{
    public const string SectionName = "MesRest";

    /// <summary>对方接收端点（如 http://mes-host/api/production-reports）。</summary>
    public string? BaseUrl { get; init; }

    /// <summary>推送超时秒（超时=结果未知，不重发）。</summary>
    public int TimeoutSeconds { get; init; } = 10;
}

public sealed class RestMesAdapter(HttpClient httpClient, IOptionsMonitor<RestMesOptions> options) : IMesAdapter
{
    public string Target => "mes.rest";

    public async Task<PushResult> PushAsync(MesEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var config = options.CurrentValue;
        if (string.IsNullOrEmpty(config.BaseUrl))
        {
            // 未配置端点 = 该 target 不可用：显式可重试失败（退避等配置），不静默丢
            return PushResult.Retryable("mes.rest: BaseUrl not configured");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

        try
        {
            // 幂等键透传给对方（6.4 铁律 2：重复推送对方必须返回「已处理」）
            using var request = new HttpRequestMessage(HttpMethod.Post, config.BaseUrl);
            request.Content = new StringContent(envelope.PayloadJson, Encoding.UTF8, "application/json");
            request.Headers.Add("X-Idempotency-Key", envelope.IdempotencyKey);
            request.Headers.Add("X-Biz-Type", envelope.BizType);
            request.Headers.Add("X-Msg-Id", envelope.Id.ToString());

            using var response = await httpClient.SendAsync(request, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            if ((int)response.StatusCode is 408 or 429 || (int)response.StatusCode >= 500)
            {
                return PushResult.Retryable($"http {(int)response.StatusCode}: {Truncate(body)}");
            }

            if (response.IsSuccessStatusCode)
            {
                return PushResult.Ok(Truncate(body));
            }

            return PushResult.Retryable($"http {(int)response.StatusCode}: {Truncate(body)}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 本适配器超时（对方没在窗口内应答）：结果未知 → 对账
            return PushResult.Unknown($"timeout after {config.TimeoutSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            // 连接失败（断网/拒连）：请求大概率没到对方 → 可重试
            return PushResult.Retryable($"connect failed: {ex.Message}");
        }
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200];
}
