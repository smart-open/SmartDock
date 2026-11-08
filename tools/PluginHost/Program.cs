// PluginHost 进程外驱动宿主（4.7 L2 最小骨架，13.1 C1）：
// - gRPC server，监听 OS 动态端口，把实际端口按 "SDGRPCPORT:nnnn" 写 stdout（主进程 Supervisor 解析约定）
// - 加载驱动 DLL（骨架阶段内置 echo 驱动，真实 32 位原生 SDK 插件随首个运控/视觉项目接入）
// - 位数校验（5.11.4 ②）：manifest "arch": "x86" 但本进程是 64 位 → 拒绝启动（Blocked）

using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Platform.Abstractions;
using Platform.PluginHost;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");   // OS 动态端口，避免与主进程/其他实例冲突
// gRPC 必须 HTTP/2；Kestrel 明文端点默认只开 HTTP/1.1，需显式 h2c（HTTP_1_1_REQUIRED 0xd 的教训）
builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureEndpointDefaults(listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});
builder.Services.AddGrpc();

// 骨架驱动（真实原生 SDK 插件随首个运控/视觉项目接入，走同一装载路径）
builder.Services.AddSingleton<IDeviceDriver>(new EchoDriver());

var app = builder.Build();
app.MapGrpcService<RemoteDriverService>();

app.Start();

// 把实际监听端口报给父进程（Supervisor.WaitForPortReportAsync 解析此行）
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
var port = new Uri(address).Port;
Console.WriteLine($"SDGRPCPORT:{port}");
Console.Out.Flush();

await app.WaitForShutdownAsync();

/// <summary>骨架期内置驱动：返回自增序列（主进程 E2E 断言用：重启后序列归零可观测子进程重建）。</summary>
internal sealed class EchoDriver : IDeviceDriver
{
    private long _tick;

    public string DriverIdValue => "driver.remote.echo";

    public DriverDescriptor Describe() =>
        new(DriverIdValue, "Echo (in-process skeleton)", SupportsWrite: false, MaxBatchPerRequest: 120);

    public Task<ConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ConnectResult(true, null));

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
}
