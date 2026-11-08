using System.Diagnostics;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Abstractions.Remote;

namespace Platform.Core.Remote;

/// <summary>宿主进程当前状态（观测面：API/日志/未来 UI 都看这个）。</summary>
public enum RemoteHostState
{
    Stopped,
    Starting,
    Running,
    Restarting,     // 崩溃/心跳超时后等待重启
}

/// <summary>
/// 进程外驱动宿主监管器（4.7 L2 + 4.8.1：宿主被 kill 后自动重启；重启进程比卸载 DLL 简单一个数量级）。
/// 职责：拉起子进程（动态端口）→ gRPC 通道 → 心跳监视 → 崩溃/僵死退避重启。
/// 不负责：驱动协议逻辑（在子进程内）；采集调度（PollingScheduler 经 RemoteDriverProxy 透明走远程）。
/// </summary>
public sealed class RemoteDriverSupervisor : IAsyncDisposable
{
    // 心跳参数：子进程正常 ping 应答 <100ms；3 连败（≈6s）判定僵死。不设太敏感——gRPC 冷启动/GC 停顿会误杀
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private const int MaxConsecutivePingFailures = 3;

    // 崩溃重启退避：与设备循环同一策略（3.2 失败策略），避免坏插件疯狂拉起
    private static readonly TimeSpan[] RestartBackoffs =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
    ];

    private readonly string _exePath;
    private readonly ILogger<RemoteDriverSupervisor> _logger;

    private Process? _process;
    private GrpcChannel? _channel;
    private RemoteDriver.RemoteDriverClient? _client;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private int _crashCount;

    public RemoteHostState State { get; private set; } = RemoteHostState.Stopped;
    public string? LastError { get; private set; }

    /// <summary>子进程 gRPC 端点（子进程启动参数把端口报告到 stdout，主进程解析——避免固定端口冲突）。</summary>
    public int? Port { get; private set; }

    public RemoteDriverSupervisor(string exePath, ILogger<RemoteDriverSupervisor> logger)
    {
        _exePath = exePath;
        _logger = logger;
    }

    /// <summary>拉起子进程并等待 gRPC 就绪。幂等：已在跑直接返回。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (State is RemoteHostState.Running or RemoteHostState.Starting)
        {
            return;
        }

        State = RemoteHostState.Starting;
        try
        {
            await SpawnAsync(cancellationToken);
            StartMonitor();
            State = RemoteHostState.Running;
            _crashCount = 0;    // 成功运行即清零退避（长时间稳定后崩溃应从短退避重新开始）
            _logger.LogInformation("remote driver host running on port {Port} (pid {Pid})",
                Port, _process?.Id);
        }
        catch (Exception ex)
        {
            State = RemoteHostState.Stopped;
            LastError = ex.Message;
            throw;
        }
    }

    private async Task SpawnAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("spawning child process: {Exe}", _exePath);
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _exePath,
                // 端口 0 = OS 分配；子进程把实际端口打上特殊前缀写到 stdout，主进程解析
                Arguments = "--urls http://127.0.0.1:0",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        _process.Start();
        // BeginOutputReadLine 必须在 Start 之后（未启动的进程没有 stdout 流）。
        // 端口等待用独立 CTS：monitor 重启路径传入的 token 已被 KillProcessAsync 取消，
        // 用它等端口会立即 OperationCanceled，子进程永远起不来（Restarting 卡死教训）
        _process.BeginOutputReadLine();
        // stderr 必须异步排空：重定向但无人读时缓冲满（~4KB）会阻塞子进程，表现为「起了但连不上」
        _ = Task.Run(async () =>
        {
            try
            {
                await _process.StandardError.ReadToEndAsync();
            }
            catch
            {
                // 进程退出/取消时读流失败属正常收尾
            }
        });
        using var spawnCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var portTask = WaitForPortReportAsync(_process, spawnCts.Token);
        _process.Exited += (_, _) => _logger.LogWarning(
            "remote driver host exited (code {Code})", _process.ExitCode);

        Port = await portTask;
        if (Port is null)
        {
            throw new InvalidOperationException("remote driver host did not report gRPC port in time");
        }

        // h2c（明文 HTTP/2）：Kestrel 的 http:// 端点就是 h2c；client 侧必须把请求
        // 版本直接设为 2.0——SocketsHttpHandler 默认对 http:// 发 HTTP/1.1，gRPC 调用
        // 会被 server 以「HTTP/1.1 request to HTTP/2-only endpoint」关连接（Internal 错误）
        var httpHandler = new SocketsHttpHandler();
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{Port}", new GrpcChannelOptions
        {
            HttpHandler = new VersionHttpHandler(httpHandler),
        });
        _client = new RemoteDriver.RemoteDriverClient(_channel);
    }

    /// <summary>从 stdout 解析 "SDGRPCPORT:nnnn" 行（子进程约定，见 PluginHost Program.cs）。</summary>
    private static async Task<int?> WaitForPortReportAsync(Process process, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null && e.Data.StartsWith("SDGRPCPORT:", StringComparison.Ordinal) &&
                int.TryParse(e.Data["SDGRPCPORT:".Length..], out var port))
            {
                tcs.TrySetResult(port);
            }
        };
        var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(tcs.Task, timeoutTask);
        return completed == tcs.Task ? tcs.Task.Result : null;
    }

    private void StartMonitor()
    {
        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_monitorCts.Token));
    }

    /// <summary>
    /// 心跳监视循环：ping 连续失败达到阈值 → 判定僵死 → 退避重启。
    /// 子进程退出事件（Exited）与此循环双保险：外部 kill 走这里的心跳失败路径统一处理。
    /// </summary>
    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(HeartbeatInterval, cancellationToken);

            var pingFailures = 0;
            var healthy = false;
            try
            {
                var reply = await _client!.PingAsync(
                    new PingRequest(),
                    deadline: DateTime.UtcNow.AddSeconds(2),
                    cancellationToken: cancellationToken);
                healthy = reply.Alive;
                if (!healthy)
                {
                    LastError = reply.LastError;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                pingFailures++;
                LastError = ex.Message;
                // 偶发一次失败立即重试（网络抖动/GC 停顿），连续 3 次才判死
                for (var attempt = 1; attempt < MaxConsecutivePingFailures && !healthy; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    try
                    {
                        var reply = await _client!.PingAsync(
                            new PingRequest(),
                            deadline: DateTime.UtcNow.AddSeconds(2),
                            cancellationToken: cancellationToken);
                        healthy = reply.Alive;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        pingFailures++;
                    }
                }
            }

            if (healthy)
            {
                continue;
            }

            _logger.LogError("remote driver host unhealthy after {Failures} ping failures (pid {Pid}), restarting",
                pingFailures, _process?.Id);
            await RestartAsync(cancellationToken);
        }
    }

    private async Task RestartAsync(CancellationToken cancellationToken)
    {
        State = RemoteHostState.Restarting;
        await KillProcessAsync();

        var backoff = RestartBackoffs[Math.Min(_crashCount, RestartBackoffs.Length - 1)];
        _crashCount++;
        _logger.LogWarning("remote driver host restarting in {Backoff} (crash #{Count})", backoff, _crashCount);
        // 退避不能用 monitor token：KillProcessAsync 已取消它，delay 会立即 OCE，
        // restart 流程死在半路（日志止于 crash #N、无 spawn 的直接原因）
        await Task.Delay(backoff);

        try
        {
            await SpawnAsync(cancellationToken);
            // KillProcessAsync 取消了旧监视循环；新进程必须配新监视，否则僵死后无人重启。
            // 先等 grace 再监视：Kestrel 就绪早于 gRPC service 注册可读，立即 ping 会把
            // 刚起来的新进程又误杀成重启循环（E2E 观测到的 Running→Restarting 振荡）
            // grace 也不用 monitor token（同 backoff：已取消的 token 会让 restart 死在半路）
            await Task.Delay(TimeSpan.FromSeconds(3));
            StartMonitor();
            State = RemoteHostState.Running;
            _logger.LogInformation("remote driver host restarted on port {Port} (pid {Pid})",
                Port, _process?.Id);
        }
        catch (Exception ex)
        {
            State = RemoteHostState.Stopped;
            LastError = ex.Message;
            _logger.LogError(ex, "remote driver host restart failed");
        }
    }

    private async Task KillProcessAsync()
    {
        if (_monitorCts is not null)
        {
            await _monitorCts.CancelAsync();
        }

        if (_process is not null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 已退出，race 无害
            }
        }

        _channel?.Dispose();
        _channel = null;
        _client = null;
    }

    /// <summary>取远程驱动代理（PollingScheduler 当普通 IDeviceDriver 用，传输透明）。</summary>
    public RemoteDriverProxy CreateProxy(string driverId) => new(this, driverId);

    internal RemoteDriver.RemoteDriverClient Client => _client
        ?? throw new InvalidOperationException("remote driver host is not running");

    public async ValueTask DisposeAsync()
    {
        if (_monitorCts is not null)
        {
            await _monitorCts.CancelAsync();
        }

        await KillProcessAsync();
        _process?.Dispose();
    }

    /// <summary>
    /// 强制 HTTP/2 明文请求版本（.NET 8 无 Http2UnencryptedSupport 开关，用 DelegatingHandler 改写版本）。
    /// </summary>
    private sealed class VersionHttpHandler : DelegatingHandler
    {
        public VersionHttpHandler(HttpMessageHandler inner) : base(inner) { }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = System.Net.HttpVersion.Version20;
            request.VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
