using Grpc.Core;
using Platform.Abstractions;
using Platform.Abstractions.Remote;

namespace Platform.Core.Remote;

/// <summary>
/// 远程驱动代理：把 gRPC 适配成 IDeviceDriver + IDeviceWriteDriver，PollingScheduler
/// 与 WriteGuard 均无感知（5.5 调度 / 5.6 写闸只认驱动契约，4.7 L2 隔离透明）。
/// 连接/读/写失败不抛（5.2/5.6 契约），转 ConnectResult/WriteResult/Bad；取消按取消语义上抛。
/// </summary>
public sealed class RemoteDriverProxy : IDeviceWriteDriver
{
    private readonly RemoteDriverSupervisor _supervisor;

    public RemoteDriverProxy(RemoteDriverSupervisor supervisor, string driverId)
    {
        _supervisor = supervisor;
        DriverIdValue = driverId;
    }

    public string DriverIdValue { get; }

    // SupportsWrite 反映的是「管道能力」：远程驱动若只读，Write RPC 返回逐点失败结果
    //（错误信息指明原因）——类型层面声明写能力 + 运行时逐点如实回报，比让 WriteGuard
    // 对远程设备一律「不支持写」更符合 5.6 的留痕语义
    public DriverDescriptor Describe() =>
        new(DriverIdValue, "remote (out-of-process)", SupportsWrite: true, MaxBatchPerRequest: 120);

    public bool IsConnected => _supervisor.State == RemoteHostState.Running;

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<ConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await _supervisor.Client.ConnectAsync(
                new ConnectRequest { EndpointJson = "{}" },
                deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
            return new ConnectResult(reply.Success, reply.Error is "" ? null : reply.Error);
        }
        catch (RpcException ex) when (ex.Status.StatusCode == StatusCode.DeadlineExceeded)
        {
            return new ConnectResult(false, "remote host timeout");
        }
        catch (RpcException ex)
        {
            return new ConnectResult(false, $"remote host unreachable: {ex.Status.StatusCode}");
        }
        catch (InvalidOperationException ex)
        {
            // 宿主被 kill 后 spawn 前的窗口：Client getter 抛「not running」，
            // 与 RPC 失败同语义归一为连接失败（5.12.3：异常不外抛）
            return new ConnectResult(false, $"remote host not running: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<ReadValue>> ReadAsync(
        IReadOnlyList<PointRef> points, CancellationToken cancellationToken = default)
    {
        var request = new ReadRequest();
        foreach (var point in points)
        {
            request.Points.Add(new PointRefDto
            {
                PointId = point.PointId,
                DeviceId = point.DeviceId,
                PointCode = point.PointCode,
                Address = point.Address,
                DataType = point.DataType,
            });
        }

        try
        {
            var reply = await _supervisor.Client.ReadAsync(
                request, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
            var values = new ReadValue[reply.Values.Count];
            for (var i = 0; i < reply.Values.Count; i++)
            {
                var dto = reply.Values[i];
                values[i] = new ReadValue(
                    dto.PointId, dto.DeviceId,
                    // Bad 值保持 null（本地驱动同口径）：proto 用 optional 表达「无值」，
                    // 否则远程链路的 Bad 落库 0.0、本地落 null，快照口径分裂
                    dto.HasValue ? dto.Value : null,
                    (QualityCode)dto.Quality,
                    DateTimeOffset.FromUnixTimeMilliseconds(dto.TimestampUnixMs).UtcDateTime);
            }

            return values;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RpcException)
        {
            // 宿主僵死/重启窗口：全部 Bad（5.7 质量码只描述真实结果）。
            // 下轮心跳触发监管器重启，不在此重试（避免读写双路并发重启逻辑）
            var now = DateTime.UtcNow;
            return points.Select(p => new ReadValue(
                p.PointId, p.DeviceId, null, QualityCode.Bad, now)).ToArray();
        }
        catch (InvalidOperationException)
        {
            var now = DateTime.UtcNow;
            return points.Select(p => new ReadValue(
                p.PointId, p.DeviceId, null, QualityCode.Bad, now)).ToArray();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<WriteResult> WriteAsync(PointRef point, double value,
        CancellationToken cancellationToken = default)
    {
        var results = await WriteBatchAsync(
            [(point, value)], cancellationToken);
        var result = results[0];
        return result;
    }

    private async Task<IReadOnlyList<WriteResult>> WriteBatchAsync(
        IReadOnlyList<(PointRef Point, double Value)> writes,
        CancellationToken cancellationToken)
    {
        var request = new WriteRequest();
        foreach (var (point, value) in writes)
        {
            request.Writes.Add(new WriteDto
            {
                PointId = point.PointId,
                DeviceId = point.DeviceId,
                PointCode = point.PointCode,
                Address = point.Address,
                DataType = point.DataType,
                Value = value,
            });
        }

        try
        {
            var reply = await _supervisor.Client.WriteAsync(
                request, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
            // 空回复/条数不符 = 协议错位（版本不匹配/服务端 bug）：results[0] 会越界抛出，
            // 违反「驱动实现不抛非取消异常」契约——按全部逐点失败返回，WriteGuard 留痕
            if (reply.Results.Count != writes.Count)
            {
                var malformed = $"malformed reply: {reply.Results.Count} results for {writes.Count} writes";
                return writes.Select(w => new WriteResult(false, malformed)).ToArray();
            }

            var results = new WriteResult[reply.Results.Count];
            for (var i = 0; i < reply.Results.Count; i++)
            {
                var dto = reply.Results[i];
                results[i] = new WriteResult(dto.Success, dto.Error is "" ? null : dto.Error);
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RpcException ex)
        {
            // 宿主僵死/重启窗口：全部逐点失败（5.6 留痕语义：失败原因进 write_command）
            var reason = $"remote host unreachable: {ex.Status.StatusCode}";
            return writes.Select(w => new WriteResult(false, reason)).ToArray();
        }
        catch (InvalidOperationException ex)
        {
            var reason = $"remote host not running: {ex.Message}";
            return writes.Select(w => new WriteResult(false, reason)).ToArray();
        }
    }

    public async IAsyncEnumerable<DeviceEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Grpc.Core.AsyncServerStreamingCall<DeviceEventDto>? call;
        try
        {
            call = _supervisor.Client.Subscribe(
                new SubscribeRequest(), cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException)
        {
            yield break;   // 宿主不在运行：事件流视为结束（订阅方随后可重试）
        }

        var stream = call.ResponseStream;
        while (true)
        {
            DeviceEventDto dto;
            try
            {
                if (!await stream.MoveNext(cancellationToken))
                {
                    break;
                }

                dto = stream.Current;
            }
            catch (RpcException ex) when (
                ex.Status.StatusCode == StatusCode.Cancelled ||
                ex.Status.StatusCode == StatusCode.Unavailable)
            {
                // 取消/宿主退出 = 流正常结束（「消费方停止枚举即退订」的契约语义）；
                // 其他 RPC 失败如实上抛，避免静默吞掉真实管道故障
                break;
            }

            yield return new DeviceEvent(
                dto.EventType, dto.DeviceId, dto.PayloadJson,
                DateTimeOffset.FromUnixTimeMilliseconds(dto.TimestampUnixMs).UtcDateTime);
        }
    }
}
