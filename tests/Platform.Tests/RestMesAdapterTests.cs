// M2-3 单测：REST 适配器分流（6.10 契约表：2xx/4xx/5xx/超时/断连）与对账告警阈值判定（6.6 任务 3）。
// 适配器用本地 HttpListener stub（真实 HTTP 栈，覆盖 HttpClient 全部分支）。

using System.Net;
using Microsoft.Extensions.Options;
using Platform.Abstractions;
using Platform.Core.Mes;
using Platform.Data;
using Xunit;

namespace Platform.Tests;

public class RestMesAdapterTests
{
    /// <summary>本地一次性 HTTP stub：按脚本应答一次请求并记录收到的头。</summary>
    private sealed class StubServer(string respondStatus, int delayMs = 0) : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string? ReceivedIdempotencyKey { get; private set; }
        public string Url { get; private set; } = "";

        public async Task<string> StartAsync()
        {
            // 端口 0 不支持 HttpListener，用随机高位端口
            var port = Random.Shared.Next(30000, 40000);
            Url = $"http://127.0.0.1:{port}/hook/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    ReceivedIdempotencyKey = context.Request.Headers["X-Idempotency-Key"];
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs);
                    }
                    context.Response.StatusCode = int.Parse(respondStatus);
                    var bytes = System.Text.Encoding.UTF8.GetBytes("stub-ack");
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch (ObjectDisposedException)
                {
                    // 正常关闭
                }
            });
            return Url;
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }

    private static RestMesAdapter CreateAdapter(string baseUrl, int timeoutSeconds = 10)
    {
        var httpClient = new HttpClient();
        var options = Options.Create(new RestMesOptions { BaseUrl = baseUrl, TimeoutSeconds = timeoutSeconds });
        return new RestMesAdapter(httpClient, new FixedMonitor(options));
    }

    private sealed class FixedMonitor(Microsoft.Extensions.Options.IOptions<RestMesOptions> options)
        : Microsoft.Extensions.Options.IOptionsMonitor<RestMesOptions>
    {
        public RestMesOptions CurrentValue => options.Value;
        public RestMesOptions Get(string? name) => options.Value;
        public IDisposable? OnChange(Action<RestMesOptions, string?> listener) => null;
    }

    private static readonly MesEnvelope Envelope = new(1, "production.report", "mes.rest", "k:1", "{}", 1);

    [Fact]
    public async Task Push2xx_GoesOk_WithAckAndIdempotencyHeader()
    {
        using var stub = new StubServer("200");
        var url = await stub.StartAsync();
        var adapter = CreateAdapter(url);

        var result = await adapter.PushAsync(Envelope);

        Assert.True(result.Success);
        Assert.Equal("stub-ack", result.AckValue);
        Assert.Equal("k:1", stub.ReceivedIdempotencyKey);   // 6.4 铁律 2：幂等键透传
    }

    [Fact]
    public async Task Push5xx_GoesRetryable()
    {
        using var stub = new StubServer("503");
        var url = await stub.StartAsync();
        var adapter = CreateAdapter(url);

        var result = await adapter.PushAsync(Envelope);

        Assert.False(result.Success);
        Assert.False(result.ResultUnknown);
        Assert.Contains("503", result.Error);
    }

    [Fact]
    public async Task Push4xx_GoesRetryableNotUnknown()
    {
        // 4xx 参数错按 6.10 表最终 Dead（M2-3 走完退避阶梯），这里锁定：不是 Unknown（不进对账挂起）
        using var stub = new StubServer("400");
        var url = await stub.StartAsync();
        var adapter = CreateAdapter(url);

        var result = await adapter.PushAsync(Envelope);

        Assert.False(result.Success);
        Assert.False(result.ResultUnknown);
        Assert.Contains("400", result.Error);
    }

    [Fact]
    public async Task PushTimeout_GoesUnknown()
    {
        // 6.10 超时行：平台转 Suspended 不重复推 —— Unknown 分流锁定
        using var stub = new StubServer("200", delayMs: 3000);
        var url = await stub.StartAsync();
        var adapter = CreateAdapter(url, timeoutSeconds: 1);

        var result = await adapter.PushAsync(Envelope);

        Assert.False(result.Success);
        Assert.True(result.ResultUnknown);
        Assert.Contains("timeout", result.Error);
    }

    [Fact]
    public async Task PushConnectRefused_GoesRetryable()
    {
        // 断网口径：连接失败可重试（对方大概率没收到）
        var adapter = CreateAdapter("http://127.0.0.1:1/hook/");   // 端口 1 必拒连

        var result = await adapter.PushAsync(Envelope);

        Assert.False(result.Success);
        Assert.False(result.ResultUnknown);
        Assert.Contains("connect failed", result.Error);
    }
}

/// <summary>
/// 6.6 对账任务 3 告警阈值：直接断言生产判据 ReconcileAlertPolicy.ShouldAlert
/// （MesReconcileStore.ReconcileAsync 落库 alert 字段就是调它），不再本地重实现规则。
/// </summary>
public class ReconcileThresholdTests
{
    [Theory]
    [InlineData(0, 100, false)]     // 全闭环：不告警
    [InlineData(1, 5000, false)]    // 0.02% < 0.5% 且 ≤10 条
    [InlineData(11, 5000, true)]    // 11 条 > 10 条 → 告警（6.6 任务 3）
    [InlineData(1, 100, true)]      // 1% > 0.5% → 告警
    [InlineData(10, 5000, false)]   // 恰 10 条且 0.2%：不告警（>10 才告）
    [InlineData(0, 0, false)]       // 当日无报文：不告警（除零保护）
    [InlineData(6, 500, true)]      // 1.2% > 0.5% 占比告警（条数不超 10 也告：两条件取或）
    public void AlertThreshold_MatchesSpec(int diffCount, int totalSent, bool expectedAlert)
    {
        Assert.Equal(expectedAlert, ReconcileAlertPolicy.ShouldAlert(diffCount, totalSent));
    }
}
