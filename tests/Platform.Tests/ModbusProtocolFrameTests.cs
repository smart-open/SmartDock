using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Driver.ModbusTcp;
using Platform.Abstractions;
using Xunit;

namespace Platform.Tests;

/// <summary>
/// Modbus TCP 驱动协议帧测试（S28.5 欠账：tid 校验/短帧/异常码/坏字节计数/畸形长度/
/// 半包/无应答超时此前零覆盖）。桩服务器按 MBAP 长度字段切帧，可编程应答。
/// </summary>
public class ModbusProtocolFrameTests
{
    private static readonly PointRef Point = new(1, 1, "p1", "40001:Float32:CDAB", "Float32");

    private static async Task<ModbusTcpDriver> ConnectAsync(int port)
    {
        var driver = new ModbusTcpDriver("127.0.0.1", port);
        var connect = await driver.ConnectAsync();
        Assert.True(connect.Success, connect.Error);
        return driver;
    }

    /// <summary>42.0f = 0x42280000；CDAB（低字在前）→ 寄存器 [0x0000, 0x4228]。</summary>
    private static byte[] SuccessPayload() => [0x04, 0x00, 0x00, 0x42, 0x28];

    /// <summary>构造 MBAP 应答：tid/unitId 回显请求，length = unit + FC + payload。</summary>
    private static byte[] ModbusReply(byte[] request, byte functionCode, params byte[] payload)
    {
        var length = 2 + payload.Length;
        var reply = new byte[8 + payload.Length];
        reply[0] = request[0];
        reply[1] = request[1];
        reply[4] = (byte)(length >> 8);
        reply[5] = (byte)length;
        reply[6] = request[6];
        reply[7] = functionCode;
        payload.CopyTo(reply, 8);
        return reply;
    }

    private static void AssertBad(IReadOnlyList<ReadValue> results)
    {
        Assert.Single(results);
        Assert.Equal(QualityCode.Bad, results[0].Quality);
        Assert.Null(results[0].Value);
    }

    [Fact]
    public async Task ReadAsync_GoodFrame_ReturnsGoodValue()
    {
        await using var server = await StubServer.StartAsync(
            (stream, request, ct) => stream.WriteAsync(ModbusReply(request, 3, SuccessPayload()), ct));
        await using var driver = await ConnectAsync(server.Port);

        var results = await driver.ReadAsync([Point]);

        Assert.Single(results);
        Assert.Equal(QualityCode.Good, results[0].Quality);
        Assert.Equal(42.0f, results[0].Value);
    }

    [Fact]
    public async Task ReadAsync_WrongTransactionId_ReturnsBad()
    {
        await using var server = await StubServer.StartAsync((stream, request, ct) =>
        {
            var wrong = ModbusReply(request, 3, SuccessPayload());
            wrong[0] = (byte)(request[0] + 1);   // tid 不匹配
            return stream.WriteAsync(wrong, ct);
        });
        await using var driver = await ConnectAsync(server.Port);

        AssertBad(await driver.ReadAsync([Point]));
    }

    [Fact]
    public async Task ReadAsync_ModbusExceptionCode_ReturnsBad()
    {
        await using var server = await StubServer.StartAsync(
            (stream, request, ct) => stream.WriteAsync(ModbusReply(request, 0x83, 0x02), ct));
        await using var driver = await ConnectAsync(server.Port);

        AssertBad(await driver.ReadAsync([Point]));
    }

    [Fact]
    public async Task ReadAsync_UnexpectedByteCount_ReturnsBad()
    {
        await using var server = await StubServer.StartAsync(
            (stream, request, ct) => stream.WriteAsync(ModbusReply(request, 3, 0x03, 0x00, 0x00, 0x42), ct));
        await using var driver = await ConnectAsync(server.Port);

        AssertBad(await driver.ReadAsync([Point]));
    }

    [Fact]
    public async Task ReadAsync_ShortPdu_ReturnsBad()
    {
        // MBAP length=2 → PDU 仅 1 字节（FC），无 byteCount：短帧保护
        await using var server = await StubServer.StartAsync(
            (stream, request, ct) => stream.WriteAsync(ModbusReply(request, 3), ct));
        await using var driver = await ConnectAsync(server.Port);

        AssertBad(await driver.ReadAsync([Point]));
    }

    [Fact]
    public async Task ReadAsync_MalformedHugeLength_ReturnsBad()
    {
        // length=0xFFFF：修复前会 new byte[65534] 并试图读 64KB，修复后上界直接拒绝
        await using var server = await StubServer.StartAsync((stream, request, ct) =>
        {
            var huge = ModbusReply(request, 3, SuccessPayload());
            huge[4] = 0xFF;
            huge[5] = 0xFF;
            return stream.WriteAsync(huge, ct);
        });
        await using var driver = await ConnectAsync(server.Port);

        AssertBad(await driver.ReadAsync([Point]));
    }

    [Fact]
    public async Task ReadAsync_FragmentedArrival_ParsesCorrectly()
    {
        // 半包：头 7 字节先到、停 150ms、余部后到——ReadExactlyAsync 必须正确拼帧
        await using var server = await StubServer.StartAsync(async (stream, request, ct) =>
        {
            var reply = ModbusReply(request, 3, SuccessPayload());
            await stream.WriteAsync(reply.AsMemory(0, 7), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(150, ct);
            await stream.WriteAsync(reply.AsMemory(7), ct);
        });
        await using var driver = await ConnectAsync(server.Port);

        var results = await driver.ReadAsync([Point]);

        Assert.Equal(QualityCode.Good, results[0].Quality);
        Assert.Equal(42.0f, results[0].Value);
    }

    [Fact]
    public async Task ReadAsync_NoReply_TimesOutAsBad()
    {
        // P0 修复验证：Socket.ReceiveTimeout 对 async IO 无效（修复前本测试永久挂起），
        // 修复后 linked CTS 3s 超时转 Bad——超时时间放 10s 防慢 CI 误报
        await using var server = await StubServer.StartAsync((_, _, _) => ValueTask.CompletedTask);
        await using var driver = await ConnectAsync(server.Port);

        var stopwatch = Stopwatch.StartNew();
        var results = await driver.ReadAsync([Point]);
        stopwatch.Stop();

        AssertBad(results);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"timeout fix broken: took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task ReadAsync_ConnectionClosedByPeer_ReturnsBad()
    {
        // 桩收到请求即断开：驱动读到 0 字节 → "connection closed by peer" → Bad
        await using var server = await StubServer.StartAsync(
            (_, _, _) => throw new IOException("stub closes connection"));
        await using var driver = await ConnectAsync(server.Port);

        AssertBad(await driver.ReadAsync([Point]));
    }

    [Fact]
    public async Task ReadAsync_UnknownByteOrder_ReturnsBadAddress()
    {
        // 地址解析先行（连接都无需建立）：未知字序不再静默当 ABCD
        await using var driver = new ModbusTcpDriver("127.0.0.1", 1);
        var results = await driver.ReadAsync([new PointRef(1, 1, "p1", "40001:Float32:XYZ", "Float32")]);

        AssertBad(results);
    }

    [Fact]
    public async Task WriteAsync_EchoedStartMatches_ReturnsSuccess()
    {
        await using var server = await StubServer.StartAsync((stream, request, ct) =>
        {
            // FC16 正常应答回显起始地址 + 数量（请求字节 8-11）
            byte[] payload = [request[8], request[9], request[10], request[11]];
            return stream.WriteAsync(ModbusReply(request, 0x10, payload), ct);
        });
        await using var driver = await ConnectAsync(server.Port);

        var result = await driver.WriteAsync(Point, 42.0);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task WriteAsync_ModbusException_ReturnsFailure()
    {
        await using var server = await StubServer.StartAsync(
            (stream, request, ct) => stream.WriteAsync(ModbusReply(request, 0x90, 0x02), ct));
        await using var driver = await ConnectAsync(server.Port);

        var result = await driver.WriteAsync(Point, 42.0);

        Assert.False(result.Success);
    }

    /// <summary>
    /// 本地 TCP 桩：按 MBAP 长度字段切帧收请求（兼容 FC3 12B / FC16 19B），
    /// 应答行为完全由测试的 handler 决定。handler 抛异常 = 断开连接。
    /// </summary>
    private sealed class StubServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private StubServer(int port, TcpListener listener, Task loop)
        {
            Port = port;
            _listener = listener;
            _loop = loop;
        }

        public int Port { get; }

        public static async Task<StubServer> StartAsync(
            Func<NetworkStream, byte[], CancellationToken, ValueTask> handleRequest)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return new StubServer(port, listener, AcceptLoopAsync(listener, handleRequest));
        }

        private static async Task AcceptLoopAsync(
            TcpListener listener, Func<NetworkStream, byte[], CancellationToken, ValueTask> handleRequest)
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(CancellationToken.None);
                    await using var stream = client.GetStream();
                    var header = new byte[7];
                    while (await ReadExactlyAsync(stream, header, CancellationToken.None))
                    {
                        var mbapLength = (header[4] << 8) | header[5];
                        if (mbapLength < 2)
                        {
                            return;   // 畸形请求：断开该连接
                        }

                        var rest = new byte[mbapLength - 1];
                        if (!await ReadExactlyAsync(stream, rest, CancellationToken.None))
                        {
                            return;
                        }

                        var request = new byte[7 + rest.Length];
                        header.CopyTo(request, 0);
                        rest.CopyTo(request, 7);
                        await handleRequest(stream, request, CancellationToken.None);
                    }
                }
            }
            catch
            {
                // 测试收尾（listener 停止）或 handler 抛异常断开：均属正常终止
            }
        }

        private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read), ct);
                if (count == 0)
                {
                    return false;   // 对端 FIN
                }

                read += count;
            }

            return true;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _cts.Cancel();
            try
            {
                await _loop;
            }
            catch
            {
                // 收尾异常不外抛
            }

            _cts.Dispose();
        }
    }
}
