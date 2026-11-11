// M1-10 压测模拟器：单进程内起 N 台 Modbus TCP 从站（每台独立端口，端口 = BasePort + 序号）。
// 单进程多 TcpListener 足以模拟 20 台设备的通信负载（每台 1 s 一个 FC03 请求），
// 且断线重连演练（杀进程 = 全部设备同时断）比逐台控制更有压力——验收判据取最坏情况。
// 协议行为与 ModbusSlaveSimulator 一致：FC03 读 + FC16 写后保持（FC03 回读已写值，
// 供 WriteGuard verified=true 判定），Float32/CDAB sine 波。

using System.Net;
using System.Net.Sockets;

var basePort = args.Length > 0 && int.TryParse(args[0], out var bp) ? bp : 5100;
var deviceCount = args.Length > 1 && int.TryParse(args[1], out var dc) ? dc : 20;

var listeners = new TcpListener[deviceCount];
// 写寄存器 bank 按「每设备」而非「每连接」持有：写命令与回读轮询即便不在同一条
// TCP 连接上（断线重连后）也能读到已写值；修复前写在连接局部数组里且 FC03 从不
// 查它，写入永远回读不到（verified=false，S28.5 欠账）
var writtenRegisters = new ushort?[deviceCount][];
for (var i = 0; i < deviceCount; i++)
{
    listeners[i] = new TcpListener(IPAddress.Any, basePort + i);
    listeners[i].Start();
    writtenRegisters[i] = new ushort?[512];
}

Console.WriteLine($"load simulator: {deviceCount} devices on ports {basePort}..{basePort + deviceCount - 1}");

var listenersSnapshot = listeners;
for (var i = 0; i < deviceCount; i++)
{
    var deviceIndex = i;
    _ = AcceptLoopAsync(listenersSnapshot[deviceIndex], deviceIndex);
}

await Task.Delay(Timeout.Infinite);

async Task AcceptLoopAsync(TcpListener listener, int deviceIndex)
{
    while (true)
    {
        try
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(client, deviceIndex, writtenRegisters[deviceIndex]);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            // listener 被主进程退出关闭时静默；运行期网络异常记日志后继续 accept
            Console.WriteLine($"device {deviceIndex}: accept error {ex.GetType().Name}");
        }
    }
}

async Task HandleClientAsync(TcpClient client, int deviceIndex, ushort?[] deviceRegisters)
{
    await using var stream = client.GetStream();
    var buffer = new byte[512];

    try
    {
        while (true)
        {
            if (!await ReadExactlyAsync(stream, buffer.AsMemory(0, 7)))
            {
                return;
            }

            var mbapLength = (buffer[4] << 8) | buffer[5];
            if (mbapLength is < 2 or > 300)
            {
                return;
            }

            var remaining = mbapLength - 1;
            if (remaining > 0 && !await ReadExactlyAsync(stream, buffer.AsMemory(7, remaining)))
            {
                return;
            }

            var functionCode = buffer[7];
            byte[] response;

            if (functionCode == 0x03)
            {
                response = BuildReadResponse(buffer, deviceIndex, deviceRegisters);
            }
            else if (functionCode == 0x10)
            {
                var start = (buffer[8] << 8) | buffer[9];
                var count = (ushort)((buffer[10] << 8) | buffer[11]);
                var byteCount = buffer[12];
                for (var i = 0; i < count && 13 + i * 2 + 1 < 13 + byteCount; i++)
                {
                    deviceRegisters[(start + (ushort)i) % deviceRegisters.Length] =
                        (ushort)((buffer[13 + i * 2] << 8) | buffer[14 + i * 2]);
                }

                response = [buffer[0], buffer[1], 0x00, 0x00, 0x00, 0x06, buffer[6], 0x10,
                            buffer[8], buffer[9], buffer[10], buffer[11]];
            }
            else
            {
                response = [buffer[0], buffer[1], 0x00, 0x00, 0x00, 0x03, buffer[6], (byte)(functionCode | 0x80), 0x01];
            }

            await stream.WriteAsync(response);
            await stream.FlushAsync();
        }
    }
    catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
    {
        Console.WriteLine($"device {deviceIndex}: client {client.Client.RemoteEndPoint} error {ex.GetType().Name}");
    }
}

byte[] BuildReadResponse(byte[] request, int deviceIndex, ushort?[] deviceRegisters)
{
    var start = (request[8] << 8) | request[9];
    var count = (ushort)((request[10] << 8) | request[11]);
    var byteCount = (byte)(count * 2);
    var response = new byte[9 + byteCount];

    Array.Copy(request, response, 7);
    response[4] = (byte)(((3 + byteCount) >> 8) & 0xFF);
    response[5] = (byte)((3 + byteCount) & 0xFF);
    response[7] = 0x03;
    response[8] = byteCount;

    for (var i = 0; i < count; i++)
    {
        var absoluteIndex = start + (uint)i;
        // 已写槽位回写实值（FC16 写后保持）；未写槽位按寄存器绝对地址生成 sine 波
        var registerValue = deviceRegisters[absoluteIndex % deviceRegisters.Length]
                            ?? GenerateRegisterValue((int)absoluteIndex, deviceIndex);
        response[9 + i * 2] = (byte)(registerValue >> 8);
        response[10 + i * 2] = (byte)(registerValue & 0xFF);
    }

    return response;
}

ushort GenerateRegisterValue(int index, int deviceIndex)
{
    // 每台设备相位偏移不同，快照值可按设备区分（压测断言的值域窗口带设备号）
    var seconds = (float)(DateTime.UtcNow.Ticks % 10000000) / 10000000f;
    var floatIndex = index / 2;
    var value = 100.0f + 50.0f * MathF.Sin(seconds * 2 * MathF.PI + floatIndex * 0.5f + deviceIndex * 0.1f);
    var raw = BitConverter.SingleToUInt32Bits(value);
    return index % 2 == 0 ? (ushort)(raw & 0xFFFF) : (ushort)(raw >> 16);
}

async Task<bool> ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer[offset..]);
        if (read == 0)
        {
            return false;
        }

        offset += read;
    }

    return true;
}
