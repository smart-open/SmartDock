// PluginHost 进程外驱动宿主（4.7 L2 最小骨架，13.1 C1）：
// - gRPC server，监听 OS 动态端口，把实际端口按 "SDGRPCPORT:nnnn" 写 stdout（主进程 Supervisor 解析约定）
// - 加载驱动 DLL（骨架阶段内置 echo 驱动，真实 32 位原生 SDK 插件随首个运控/视觉项目接入）
// - 位数校验（5.11.4 ②）：manifest "arch": "x86" 但本进程是 64 位 → 拒绝启动（Blocked）

using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Platform.Abstractions;
using Platform.Abstractions.Remote;
using Platform.PluginHost;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");   // OS 动态端口，避免与主进程/其他实例冲突
// gRPC 必须 HTTP/2；Kestrel 明文端点默认只开 HTTP/1.1，需显式 h2c（HTTP_1_1_REQUIRED 0xd 的教训）
builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureEndpointDefaults(listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});
builder.Services.AddGrpc();

// 骨架驱动（真实原生 SDK 插件随首个运控/视觉项目接入，走同一装载路径）。
// 写与事件通道随 M1-8 全契约补齐：EchoDriver 实现可写 + 事件源，供 E2E 断言管道
builder.Services.AddSingleton<EchoDriver>();
builder.Services.AddSingleton<IDeviceDriver>(sp => sp.GetRequiredService<EchoDriver>());
builder.Services.AddSingleton<IDeviceEventSource>(sp => sp.GetRequiredService<EchoDriver>());

var app = builder.Build();
app.MapGrpcService<RemoteDriverService>();

app.Start();

// 把实际监听端口报给父进程（Supervisor.WaitForPortReportAsync 解析此行）
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
var port = new Uri(address).Port;
Console.WriteLine($"SDGRPCPORT:{port}");
Console.Out.Flush();

await app.WaitForShutdownAsync();

/// <summary>
/// 骨架期内置驱动：读返回自增序列（重启后归零可观测子进程重建），
/// 写记录最近值并回报成功，连接/写动作产生事件（Subscribe 管道 E2E 断言用）。
/// </summary>
public sealed class EchoDriver : IDeviceWriteDriver, IDeviceEventSource
{
    private long _tick;
    private readonly System.Threading.Channels.Channel<DeviceEvent> _events =
        System.Threading.Channels.Channel.CreateUnbounded<DeviceEvent>();

    // gRPC 每调用独立线程：并发 Write RPC 会并发写普通 Dictionary（M1-10 同款桶损坏），
    // 必须用并发容器
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, double> _lastWritten = new();

    /// <summary>事件流（RemoteDriverService.Subscribe 经此转发；unbounded 通道骨架期无背压需求）。</summary>
    public async IAsyncEnumerable<DeviceEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var ev in _events.Reader.ReadAllAsync(cancellationToken))
        {
            yield return ev;
        }
    }

    public string DriverIdValue => "driver.remote.echo";

    public DriverDescriptor Describe() =>
        new(DriverIdValue, "Echo (in-process skeleton)", SupportsWrite: true, MaxBatchPerRequest: 120);

    public bool IsConnected => true;   // 虚拟驱动：永远在线

    public Task<ConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        Publish("connection.changed", 0, """{"connected":true}""");
        return Task.FromResult(new ConnectResult(true, null));
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ReadValue>> ReadAsync(
        IReadOnlyList<PointRef> points, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var results = new ReadValue[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var tick = System.Threading.Interlocked.Increment(ref _tick);
            results[i] = new ReadValue(points[i].PointId, points[i].DeviceId, tick,
                QualityCode.Good, now);
        }

        return Task.FromResult<IReadOnlyList<ReadValue>>(results);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<WriteResult> WriteAsync(PointRef point, double value,
        CancellationToken cancellationToken = default)
    {
        _lastWritten[point.PointId] = value;
        Publish("write.completed", point.DeviceId,
            System.Text.Json.JsonSerializer.Serialize(new { point.PointId, value }));
        return Task.FromResult(new WriteResult(true, null));
    }

    private void Publish(string eventType, long deviceId, string payloadJson)
    {
        // TryWrite 永远成功（unbounded）；测试驱动不会成为内存压力源
        _events.Writer.TryWrite(new DeviceEvent(eventType, deviceId, payloadJson, DateTime.UtcNow));
    }
}
