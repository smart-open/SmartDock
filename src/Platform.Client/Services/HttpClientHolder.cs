namespace Platform.Client.Services;

using System.Collections.Concurrent;
using System.Net.Http;

/// <summary>
/// 共享 HttpClient 池（审查修复项）：页面随导航重建，原实现每个 ApiClient 每次
/// 导航 new 一个 HttpClient 且从不释放——7×24 上位机反复导航会堆积连接池，
/// 长会话有端口耗尽风险。按「baseAddress+超时」复用同一实例；
/// SocketsHttpHandler 设 PooledConnectionLifetime 定期刷新 DNS/陈旧连接。
/// HttpClient 线程安全，可跨页面并发使用。
/// </summary>
public static class HttpClientHolder
{
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new(StringComparer.Ordinal);

    public static HttpClient Get(string baseAddress, TimeSpan timeout)
        => Clients.GetOrAdd($"{baseAddress}|{timeout.TotalSeconds}", _ => new HttpClient(
            new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            })
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = timeout,
        });
}
