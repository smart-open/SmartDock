using System.Net;
using System.Net.Sockets;

// 进程内 Modbus TCP 从站模拟器（M0 自测用）：
// 保持寄存器 0~19 按 sine 波动（Float32 可读），支持 FC03 读保持寄存器。
var listener = new TcpListener(IPAddress.Any, 5020);
listener.Start();
Console.WriteLine("Modbus TCP slave simulator listening on 0.0.0.0:5020 (FC03, unit 1, 20 registers)");

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
            if (!await ReadExactlyAsync(stream, buffer.AsMemory(0, 7)))
            {
                break;
            }

            // MBAP length = unitId(1) + PDU；请求 PDU 对 FC03 = 5 字节 → length=6
            var mbapLength = (buffer[4] << 8) | buffer[5];
            if (mbapLength is < 2 or > 300)
            {
                Console.WriteLine($"bad MBAP length {mbapLength}, bytes: {Convert.ToHexString(buffer.AsSpan(0, 7))}");
                break;
            }

            var remaining = mbapLength - 1;   // 从 unitId 起全读（PDU 含 unit 后所有字节）
            if (remaining > 0 && !await ReadExactlyAsync(stream, buffer.AsMemory(7, remaining)))
            {
                break;
            }

            var functionCode = buffer[7];
            byte[] response;

            if (functionCode == 0x03)
            {
                var start = (buffer[8] << 8) | buffer[9];
                var count = (ushort)((buffer[10] << 8) | buffer[11]);
                response = BuildReadResponse(buffer, count);
                Console.WriteLine($"FC03 read {count} registers @ {start}");
            }
            else
            {
                // 不支持的功能码 → Modbus 异常响应
                response = [buffer[0], buffer[1], 0x00, 0x00, 0x00, 0x03, buffer[6], (byte)(functionCode | 0x80), 0x01];
            }

            await stream.WriteAsync(response);
            await stream.FlushAsync();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"client error: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
    }

    Console.WriteLine("client disconnected");
}

byte[] BuildReadResponse(byte[] request, ushort count)
{
    var byteCount = (byte)(count * 2);
    var response = new byte[9 + byteCount];

    Array.Copy(request, response, 7);   // MBAP 回显
    response[4] = (byte)(((3 + byteCount) >> 8) & 0xFF);
    response[5] = (byte)((3 + byteCount) & 0xFF);
    response[7] = 0x03;
    response[8] = byteCount;

    for (var i = 0; i < count; i++)
    {
        var registerValue = GenerateRegisterValue(i);
        response[9 + i * 2] = (byte)(registerValue >> 8);
        response[10 + i * 2] = (byte)(registerValue & 0xFF);
    }

    return response;
}

// 生成动态寄存器值：偶数寄存器 = sine 低字，奇数 = sine 高字（CDAB 配对成 Float32）
ushort GenerateRegisterValue(int index)
{
    var seconds = (float)(DateTime.UtcNow.Ticks % 10000000) / 10000000f;
    var floatIndex = index / 2;
    var value = 100.0f + 50.0f * MathF.Sin(seconds * 2 * MathF.PI + floatIndex * 0.5f);
    var raw = BitConverter.SingleToUInt32Bits(value);

    // CDAB：低字在前
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
