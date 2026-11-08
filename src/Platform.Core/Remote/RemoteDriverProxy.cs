using Grpc.Core;
using Platform.Abstractions;
using Platform.Abstractions.Remote;

namespace Platform.Core.Remote;

/// <summary>
/// 远程驱动代理：把 gRPC 适配成 IDeviceDriver，PollingScheduler 无感知
/// （5.5 调度只认驱动契约，4.7 L2 隔离对调度透明）。
/// 连接/读失败不抛（5.2 驱动契约），转 ConnectResult/Bad；取消按取消语义上抛。
/// </summary>
public sealed class RemoteDriverProxy : IDeviceDriver
{
    private readonly RemoteDriverSupervisor _supervisor;

    public RemoteDriverProxy(RemoteDriverSupervisor supervisor, string driverId)
    {
        _supervisor = supervisor;
        DriverIdValue = driverId;
    }

    public string DriverIdValue { get; }

    public DriverDescriptor Describe() =>
        new(DriverIdValue, "remote (out-of-process)", SupportsWrite: false, MaxBatchPerRequest: 120);

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
                    dto.PointId, dto.DeviceId, dto.Value,
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
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
