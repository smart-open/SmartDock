using Grpc.Core;
using Platform.Abstractions;
using Platform.Abstractions.Remote;

namespace Platform.PluginHost;

/// <summary>
/// gRPC 服务端：把驱动实现包成进程外服务（4.7 L2 进程外契约的宿主侧）。
/// Ping/Connect/Read 直接转发内置驱动；写与事件订阅随真实原生驱动插件补。
/// </summary>
public sealed class RemoteDriverService : RemoteDriver.RemoteDriverBase
{
    private readonly IDeviceDriver _driver;
    private string? _lastError;

    public RemoteDriverService(IDeviceDriver driver) => _driver = driver;

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
}
