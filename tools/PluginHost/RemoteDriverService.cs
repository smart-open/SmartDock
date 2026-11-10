using Grpc.Core;
using Platform.Abstractions;
using Platform.Abstractions.Remote;

namespace Platform.PluginHost;

/// <summary>
/// gRPC 服务端：把驱动实现包成进程外服务（4.7 L2 进程外契约的宿主侧）。
/// Ping/Connect/Read/Subscribe 按契约接口转发；写只对实现 IDeviceWriteDriver 的驱动开放。
/// </summary>
public sealed class RemoteDriverService : RemoteDriver.RemoteDriverBase
{
    private readonly IDeviceDriver _driver;
    private readonly IDeviceEventSource _eventSource;
    private string? _lastError;

    public RemoteDriverService(IDeviceDriver driver, IDeviceEventSource eventSource)
    {
        _driver = driver;
        _eventSource = eventSource;
    }

    public override Task<PingReply> Ping(PingRequest request, ServerCallContext context)
        => Task.FromResult(new PingReply { Alive = true, LastError = _lastError ?? "" });

    public override async Task<ConnectReply> Connect(ConnectRequest request, ServerCallContext context)
    {
        var result = await _driver.ConnectAsync(context.CancellationToken);
        _lastError = result.Error;
        return new ConnectReply { Success = result.Success, Error = result.Error ?? "" };
    }

    public override async Task<ReadReply> Read(ReadRequest request, ServerCallContext context)
    {
        var points = request.Points.Select(dto => new PointRef(
            dto.PointId, dto.DeviceId, dto.PointCode, dto.Address, dto.DataType)).ToArray();
        var values = await _driver.ReadAsync(points, context.CancellationToken);

        var reply = new ReadReply();
        foreach (var value in values)
        {
            reply.Values.Add(new ReadValueDto
            {
                PointId = value.PointId,
                DeviceId = value.DeviceId,
                Value = value.Value ?? 0,
                Quality = (int)value.Quality,
                TimestampUnixMs = new DateTimeOffset(value.TimestampUtc).ToUnixTimeMilliseconds(),
                // Bad 诊断原因：M1 契约只落值+质量+时间，error 暂不外传（与平台快照口径一致）
                Error = "",
            });
        }

        return reply;
    }

    public override async Task<WriteReply> Write(WriteRequest request, ServerCallContext context)
    {
        var reply = new WriteReply();
        foreach (var dto in request.Writes)
        {
            var point = new PointRef(dto.PointId, dto.DeviceId, dto.PointCode, dto.Address, dto.DataType);
            if (_driver is IDeviceWriteDriver writeDriver)
            {
                // IDeviceWriteDriver 契约（5.2/5.6）：写失败不抛，逐点返回结果
                var result = await writeDriver.WriteAsync(point, dto.Value, context.CancellationToken);
                reply.Results.Add(new WriteResultDto
                {
                    PointId = dto.PointId,
                    Success = result.Success,
                    Error = result.Error ?? "",
                });
            }
            else
            {
                // 只读驱动：如实逐点拒绝（5.6 留痕语义——失败原因进 write_command）
                reply.Results.Add(new WriteResultDto
                {
                    PointId = dto.PointId,
                    Success = false,
                    Error = "driver does not support write",
                });
            }
        }

        return reply;
    }

    public override async Task Subscribe(
        SubscribeRequest request,
        IServerStreamWriter<DeviceEventDto> responseStream,
        ServerCallContext context)
    {
        // 事件转发：驱动事件源 → gRPC 流；客户端断开（ct 取消）即退订退出
        await foreach (var ev in _eventSource.SubscribeAsync(context.CancellationToken))
        {
            await responseStream.WriteAsync(new DeviceEventDto
            {
                EventType = ev.EventType,
                DeviceId = ev.DeviceId,
                PayloadJson = ev.PayloadJson,
                TimestampUnixMs = new DateTimeOffset(ev.TimestampUtc).ToUnixTimeMilliseconds(),
            }, context.CancellationToken);
        }
    }
}
