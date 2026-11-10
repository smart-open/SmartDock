using System.Diagnostics;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Abstractions.Remote;
using Platform.Abstractions;
using Platform.Core.Remote;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Tests;

/// <summary>
/// 4.7 L2 进程外宿主 E2E（真实子进程）：拉起 PluginHost → gRPC 读 → 强杀 → 心跳检测 → 自动重启 → 读恢复。
/// 需要 PluginHost 已构建（CI 顺序：build 全解决方案后跑测试）。
/// </summary>
public class RemoteSupervisorE2eTests
{
    private readonly ITestOutputHelper _output;

    public RemoteSupervisorE2eTests(ITestOutputHelper output) => _output = output;

    private static string FindPluginHostExe()
    {
        // tests/Platform.Tests/bin/Debug/net8.0 → 上 4 级 = 仓库根
        var repoRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        var exe = Path.GetFullPath(Path.Combine(repoRoot, "tools", "PluginHost", "bin", "Debug", "net8.0", "PluginHost.exe"));
        Assert.True(File.Exists(exe), $"PluginHost.exe not built: {exe}");
        return exe;
    }

    [Fact]
    public async Task Supervisor_KillChild_HeartbeatDetects_RestartsAndRecovers()
    {
        // 真实 logger：restart 链路的 Exited/spawn 失败必须可见（NullLogger 吞掉关键诊断）
        var testLogger = new TestOutputLogger<RemoteDriverSupervisor>(_output);
        await using var supervisor = new RemoteDriverSupervisor(FindPluginHostExe(),
            testLogger);
        await supervisor.StartAsync();
        Assert.Equal(RemoteHostState.Running, supervisor.State);
        _output.WriteLine($"started: port={supervisor.Port}");

        var proxy = supervisor.CreateProxy("driver.remote.echo");
        var points = new[] { new PointRef(1, 1, "p1", "x", "Float32") };

        // 1) 正常读（gRPC 全链路通）
        var first = await proxy.ReadAsync(points);
        if (first[0].Quality != QualityCode.Good)
        {
            // 诊断：直接打一次裸 gRPC 调用，把真实 RpcException 状态暴露出来
            var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{supervisor.Port}");
            var client = new RemoteDriver.RemoteDriverClient(channel);
            try
            {
                var ping = await client.PingAsync(new PingRequest(), deadline: DateTime.UtcNow.AddSeconds(3));
                _output.WriteLine($"diag ping: alive={ping.Alive}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"diag ping failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Assert.Equal(QualityCode.Good, first[0].Quality);

        // 2) 强杀子进程 → 监管器必须进入 Restarting（心跳判死语义验证）
        _output.WriteLine("killing child process...");
        var killed = false;
        foreach (var process in Process.GetProcessesByName("PluginHost"))
        {
            process.Kill();
            process.Dispose();
            killed = true;
        }

        Assert.True(killed, "child process not found — spawn failed silently");

        // 3) 心跳判死（3 败 ≈6s）+ 退避重启 + 子进程冷启动：40s 内必须恢复 Good 读
        var recovered = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(2000, cts.Token);
            if (supervisor.State != RemoteHostState.Running)
            {
                continue;   // 判死/退避中
            }

            var after = await proxy.ReadAsync(points);
            if (after[0].Quality == QualityCode.Good)
            {
                recovered = true;
                _output.WriteLine($"recovered: port={supervisor.Port} value={after[0].Value}");
                break;
            }
        }

        Assert.True(recovered, "supervisor did not restart child within 40s");
    }

    [Fact]
    public async Task Supervisor_WriteAndSubscribe_FullContractThroughProxy()
    {
        // 4.7 L2 全契约：写（WriteGuard 远程路径）+ 事件订阅，经真实子进程 gRPC
        var testLogger = new TestOutputLogger<RemoteDriverSupervisor>(_output);
        await using var supervisor = new RemoteDriverSupervisor(FindPluginHostExe(), testLogger);
        await supervisor.StartAsync();

        var proxy = supervisor.CreateProxy("driver.remote.echo");
        var point = new PointRef(7, 3, "setpoint", "HR100", "Float32");

        // 1) 先订事件再动作（保证事件不因订阅晚了而漏收）
        var received = new System.Collections.Concurrent.ConcurrentQueue<DeviceEvent>();
        using var subscribeCts = new CancellationTokenSource();
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var ev in proxy.SubscribeAsync(subscribeCts.Token))
            {
                received.Enqueue(ev);
            }
        });
        await Task.Delay(500);   // gRPC 流建立窗口

        // 2) 写（代理 IDeviceWriteDriver 形态，WriteGuard 实际调用的签名）
        var write = await ((IDeviceWriteDriver)proxy).WriteAsync(point, 42.5);
        Assert.True(write.Success, $"write failed: {write.Error}");
        _output.WriteLine("write ok");

        // 3) 连接（EchoDriver 发 connection.changed 事件）
        var connect = await proxy.ConnectAsync();
        Assert.True(connect.Success);

        // 4) 事件到达断言：write.completed（含写入值）+ connection.changed
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline &&
               (!received.Any(ev => ev.EventType == "write.completed") ||
                !received.Any(ev => ev.EventType == "connection.changed")))
        {
            await Task.Delay(200);
        }

        subscribeCts.Cancel();
        try { await subscribeTask; } catch (OperationCanceledException) { }

        var events = received.ToArray();
        _output.WriteLine($"events: {string.Join(", ", events.Select(ev => $"{ev.EventType}@dev{ev.DeviceId} {ev.PayloadJson}"))}");
        Assert.Contains(events, ev => ev.EventType == "write.completed" && ev.DeviceId == 3 && ev.PayloadJson.Contains("42.5"));
        Assert.Contains(events, ev => ev.EventType == "connection.changed");
        Assert.All(events, ev => Assert.True(ev.TimestampUtc > DateTime.MinValue));
    }

    private sealed class TestOutputLogger<T>(ITestOutputHelper output)
        : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => output.WriteLine($"[{logLevel}] {formatter(state, exception)}{(exception is null ? "" : $" :: {exception.GetType().Name}: {exception.Message}")}");
    }
}
