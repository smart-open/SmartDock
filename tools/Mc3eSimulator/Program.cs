using System.Net;
using System.Net.Sockets;

// 进程内三菱 MC 3E（二进制）PLC 模拟器（M1-5 自测用）：
// D 寄存器 0~39 按 sine 波动（Float32 可读），支持 0x0401 批量读（字单位）。
// 帧格式与 Driver.Mc3e 一致：副头部 0x50 0x00，全部多字节数值小端序。
var listener = new TcpListener(IPAddress.Any, 6020);
listener.Start();
Console.WriteLine("MC 3E simulator listening on 0.0.0.0:6020 (0x0401 batch read, D0~D39)");

while (true)
{
    var tcpClient = await listener.AcceptTcpClientAsync();
    _ = HandleClientAsync(tcpClient);
}

async Task HandleClientAsync(TcpClient client)
{
    Console.WriteLine($"client connected: {client.Client.RemoteEndPoint}");
    await using var stream = client.GetStream();
    var buffer = new byte[512];

    try
    {
        while (true)
        {
            // 3E 请求固定 20 字节（副头部 2 + 网络号 1 + PC 号 1 + 目标 I/O 2 + 目标站 1
            // + 监视定时器 2 + 命令 2 + 子命令 2 + 软元件代码 1 + 软元件编号 3 + 点数 3）
            const int requestLength = 20;
            if (!await ReadExactlyAsync(stream, buffer.AsMemory(0, requestLength)))
            {
                break;
            }

            if (buffer[0] != 0x50)
            {
                Console.WriteLine($"bad sub-header 0x{buffer[0]:X2}");
                break;
            }

            // 命令（小端）：0x0401 = 批量读
            var command = (ushort)(buffer[9] | (buffer[10] << 8));
            if (command != 0x0401)
            {
                await WriteErrorResponseAsync(stream, buffer, endCode: 0xC059);   // 命令不支持
                continue;
            }

            var deviceCode = buffer[13];
            var deviceNumber = (uint)(buffer[14] | (buffer[15] << 8) | (buffer[16] << 16));
            var wordCount = (ushort)(buffer[17] | (buffer[18] << 8) | (buffer[19] << 16));

            // 只实现 D 软元件（0xA8）；其他软元件回错误码（软元件指定异常）
            if (deviceCode != 0xA8 || deviceNumber + wordCount > 40)
            {
                await WriteErrorResponseAsync(stream, buffer, endCode: 0xC058);
                Console.WriteLine($"read D{deviceNumber} x{wordCount}: rejected (code=0x{deviceCode:X2})");
                continue;
            }

            // 应答 = 9 字节头 + 结束代码(2) + 数据
            var response = new byte[9 + 2 + wordCount * 2];
            Array.Copy(buffer, response, 9);   // 头部回显
            response[9] = 0x00;                // 结束代码 0 = 成功（小端）
            response[10] = 0x00;

            for (var i = 0; i < wordCount; i++)
            {
                var word = GenerateWordValue((int)deviceNumber + i);
                response[11 + i * 2] = (byte)(word & 0xFF);        // 小端
                response[12 + i * 2] = (byte)(word >> 8);
            }

            await stream.WriteAsync(response);
            await stream.FlushAsync();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"client error: {ex.GetType().Name}: {ex.Message}");
    }

    Console.WriteLine("client disconnected");
}

// 生成动态字值：偶数字 = sine 低字，奇数字 = sine 高字（CDAB 配对成 Float32）
ushort GenerateWordValue(int index)
{
    var seconds = (float)(DateTime.UtcNow.Ticks % 10000000) / 10000000f;
    var floatIndex = index / 2;
    var value = 200.0f + 30.0f * MathF.Sin(seconds * 2 * MathF.PI + floatIndex * 0.4f);
    var raw = BitConverter.SingleToUInt32Bits(value);

    // CDAB：低字在前
    return index % 2 == 0 ? (ushort)(raw & 0xFFFF) : (ushort)(raw >> 16);
}

async Task WriteErrorResponseAsync(NetworkStream stream, byte[] request, ushort endCode)
{
    var response = new byte[11];
    Array.Copy(request, response, 9);
    response[9] = (byte)(endCode & 0xFF);
    response[10] = (byte)(endCode >> 8);
    await stream.WriteAsync(response);
    await stream.FlushAsync();
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
