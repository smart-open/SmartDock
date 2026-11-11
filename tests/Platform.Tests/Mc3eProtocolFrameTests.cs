using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Driver.Mc3e;
using Platform.Abstractions;
using Xunit;

namespace Platform.Tests;

/// <summary>
/// 三菱 MC 3E 驱动协议帧测试（S28.5 欠账：副头校验/结束代码/半包/无应答超时此前零覆盖）。
/// 桩服务器按 3E 帧定长 20 字节切请求，应答行为完全可编程。
/// </summary>
public class Mc3eProtocolFrameTests
{
    private const int RequestFrameLength = 20;   // 3E 帧读请求定长（驱动 ReadWordsAsync 同口径）

    private static readonly PointRef Point = new(1, 1, "p1", "D100:Float32:CDAB", "Float32");

    private static async Task<Mc3eDriver> ConnectAsync(int port)
    {
        var driver = new Mc3eDriver("127.0.0.1", port);
        var connect = await driver.ConnectAsync();
        Assert.True(connect.Success, connect.Error);
        return driver;
    }

    /// <summary>42.0f = 0x42280000；CDAB（低字在前）→ words[0]=0x0000、words[1]=0x4228，小端字节 [00 00 28 42]。</summary>
    private static byte[] SuccessData() => [0x00, 0x00, 0x28, 0x42];

    /// <summary>构造 3E 应答：头 9 字节固定 + 监视定时器回显请求 + 结束代码（小端）+ 数据。</summary>
    private static byte[] Mc3eReply(byte[] request, ushort endCode, params byte[] data)
    {
        var reply = new byte[11 + data.Length];
        reply[0] = 0x50;
        reply[1] = 0x00;
        reply[2] = 0x00;
        reply[3] = 0xFF;
        reply[4] = 0xFF;
        reply[5] = 0x03;
        reply[6] = 0x00;
        reply[7] = request[7];
        reply[8] = request[8];
        reply[9] = (byte)(endCode & 0xFF);
        reply[10] = (byte)(endCode >> 8);
        data.CopyTo(reply, 11);
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
            (stream, request, ct) => stream.WriteAsync(Mc3eReply(request, endCode: 0, SuccessData()), ct));
        await using var driver = await ConnectAsync(server.Port);

        var results = await driver.ReadAsync([Point]);

        Assert.Single(results);
        Assert.Equal(QualityCode.Good, results[0].Quality);
        Assert.Equal(42.0f, results[0].Value);
    }

    [Fact]
    public async Task ReadAsync_WrongSubHeader_ReturnsBad()
    {
        await using var server = await StubServer.StartAsync((stream, request, ct) =>
        {
            var wrong = Mc3eReply(request, endCode: 0, SuccessData());
            wrong[0] = 0xD0;   // 副头部不匹配（如混入 2C/4D 帧或乱序字节流）
            return stream.WriteAsync(wrong, ct);
        });
        await using var driver = await ConnectAsync(server.Port);

        AssertBad(await driver.ReadAsync([Point]));
    }

    [Fact]
    public async Task ReadAsync_NonZeroEndCode_ReturnsBad()
    {
        await using var server = await StubServer.StartAsync(
            (stream, request, ct) => stream.WriteAsync(Mc3eReply(request, endCode: 0x1234), ct));
        await using var driver = await ConnectAsync(server.Port);

        AssertBad(await driver.ReadAsync([Point]));
    }

    [Fact]
    public async Task ReadAsync_FragmentedArrival_ParsesCorrectly()
    {
        // 半包：头 11 字节先到、停 150ms、数据后到——ReadExactlyAsync 必须正确拼帧
        await using var server = await StubServer.StartAsync(async (stream, request, ct) =>
        {
            var reply = Mc3eReply(request, endCode: 0, SuccessData());
            await stream.WriteAsync(reply.AsMemory(0, 11), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(150, ct);
            await stream.WriteAsync(reply.AsMemory(11), ct);
        });
        await using var driver = await ConnectAsync(server.Port);

        var results = await driver.ReadAsync([Point]);

        Assert.Equal(QualityCode.Good, results[0].Quality);
        Assert.Equal(42.0f, results[0].Value);
    }

    [Fact]
    public async Task ReadAsync_NoReply_TimesOutAsBad()
    {
        // P0 修复验证（与 ModbusTcp 同构）：async IO 不吃 Socket.ReceiveTimeout，
        // 修复前本测试永久挂起；修复后 3s 超时转 Bad——上限放 10s 防慢 CI 误报
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
        await using var server = await StubServer.StartAsync(
            (_, _, _) => throw new IOException("stub closes connection"));
        await using var driver = await ConnectAsync(server.Port);

        AssertBad(await driver.ReadAsync([Point]));
    }

    [Fact]
    public async Task ReadAsync_UnknownByteOrder_ReturnsBadAddress()
    {
        // 地址解析先行：未知字序必须报 bad address（配置错误不能被连接状态掩盖）
        await using var driver = new Mc3eDriver("127.0.0.1", 1);
        var results = await driver.ReadAsync([new PointRef(1, 1, "p1", "D100:Float32:XYZ", "Float32")]);

        AssertBad(results);
    }

    /// <summary>
    /// 本地 TCP 桩：3E 读请求定长 20 字节，按长度切帧收请求；
    /// handler 抛异常 = 断开连接（驱动侧读到 FIN 归并「连接已死」）。
    /// </summary>
    private sealed class StubServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
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
                    var request = new byte[RequestFrameLength];
                    while (await ReadExactlyAsync(stream, request, CancellationToken.None))
                    {
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
            try
            {
                await _loop;
            }
            catch
            {
                // 收尾异常不外抛
            }
        }
    }
}
