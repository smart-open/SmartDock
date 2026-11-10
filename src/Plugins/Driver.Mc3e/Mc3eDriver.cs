using System.Net.Sockets;
using Platform.Abstractions;

namespace Driver.Mc3e;

/// <summary>
/// 三菱 MC 协议 3E 帧（二进制）驱动（02 §5.2 驱动契约；M1-5 第二个托管协议）。
/// 只读软元件 D/W/ZR（字单位），Float32 = 相邻两字 CDAB 组合；
/// 连接管理/重试/落库归平台（5.12）。半双工请求-应答，与 ModbusTcpDriver 同构。
/// </summary>
public sealed class Mc3eDriver : IDeviceDriver
{
    /// <summary>驱动全局唯一标识（device.driver_id 引用）。</summary>
    public const string DriverId = "driver.mc3e";

    // 3E 帧副头部（二进制）：帧格式固定 + 网络号/PC 号/请求目标 I/O 常量（Q 系单机直连场景）
    private const byte FrameSubHeader = 0x50;      // 0x50 0x00 = 3E 帧二进制
    private const byte NetworkNo = 0x00;
    private const byte PcNo = 0xFF;
    private const byte RequestDestModuleIo = 0xFF; // 0xFF 0x03 = PLCCPU
    private const byte RequestDestModuleStation = 0x00;
    private const ushort CpuWatchdogTimer = 0x0010;   // ×250ms = 4s PLC 应答超时

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);   // 半双工：请求-应答严格串行
    // 连接状态变更串行化（与 ModbusTcpDriver 同因：采集循环/Dispose 并发竞态）
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    // 请求-应答传输层超时：半开连接时 ReadAsync 会无限挂起，采集循环静默停产（5.8）
    private const int SocketTimeoutMs = 3000;

    private readonly string _host;
    private readonly int _port;
    private readonly int _plcStationTimeoutMs;
    private string? _lastError;

    public Mc3eDriver(string host, int port = 6000, int plcStationTimeoutMs = 4000)
    {
        _host = host;
        _port = port;
        _plcStationTimeoutMs = plcStationTimeoutMs;
    }

    public DriverDescriptor Describe() =>
        new(DriverId, "Mitsubishi MC 3E", SupportsWrite: false, MaxBatchPerRequest: 192);

    public bool IsConnected => _tcpClient?.Connected == true;

    public async Task<ConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
            {
                return new ConnectResult(true, null);
            }

            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(_host, _port, cancellationToken);
                client.SendTimeout = SocketTimeoutMs;
                client.ReceiveTimeout = SocketTimeoutMs;
                _tcpClient = client;
                _stream = client.GetStream();
                return new ConnectResult(true, null);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                return new ConnectResult(false, ex.Message);
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            _stream?.Dispose();
            _stream = null;
            _tcpClient?.Dispose();
            _tcpClient = null;
            await Task.CompletedTask;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<IReadOnlyList<ReadValue>> ReadAsync(
        IReadOnlyList<PointRef> points,
        CancellationToken cancellationToken = default)
    {
        var results = new ReadValue[points.Count];

        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var (pointRef, index) in points.Select((p, i) => (p, i)))
            {
                results[index] = await ReadSingleAsync(pointRef, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        return results;
    }

    private async Task<ReadValue> ReadSingleAsync(PointRef point, CancellationToken cancellationToken)
    {
        // 地址解析先行：坏地址与连接状态无关，必须始终报 "bad address"（配置错误不能被连接状态掩盖）
        var parsed = ParseAddress(point.Address);
        if (parsed is null)
        {
            return Bad(point, $"bad address: {point.Address}");
        }

        if (_stream is null)
        {
            return Bad(point, "not connected");
        }

        try
        {
            var words = await ReadWordsAsync(parsed.Value.DeviceCode, parsed.Value.DeviceNumber,
                parsed.Value.WordCount, cancellationToken);
            var value = ParseFloat32(words, swapWords: parsed.Value.SwapWords);
            return new ReadValue(point.PointId, point.DeviceId, value, QualityCode.Good, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            // 通信异常大概率连接已死：主动断开，让下轮轮询走重连（与 ModbusTcpDriver 同策略）
            await DisconnectAsync(CancellationToken.None);
            return Bad(point, ex.Message);
        }
        // OperationCanceledException 不算读失败：停机不产生假 Bad 记录（与 ModbusTcpDriver 同策略）
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>
    /// 3E 帧读软元件：请求 = 副头部(2) 网络号(1) PC号(1) 目标I/O(2) 目标站(1) 监视定时器(2)
    /// + 命令(2) 子命令(2) 软元件代码(1) 软元件点数(3)；
    /// 应答 = 同头部 + 结束代码(2) + 数据。二进制帧所有多字节数值小端序。
    /// </summary>
    private async Task<ushort[]> ReadWordsAsync(
        byte deviceCode, uint deviceNumber, ushort wordCount, CancellationToken cancellationToken)
    {
        // 副头部(2)+网络号(1)+PC号(1)+目标I/O(2)+目标站(1)+监视定时器(2)
        // +命令(2)+子命令(2)+软元件代码(1)+软元件编号(3)+软元件点数(3) = 20 字节
        const int requestLength = 2 + 1 + 1 + 2 + 1 + 2 + 2 + 2 + 1 + 3 + 3;

        var request = new byte[requestLength];
        request[0] = FrameSubHeader;
        request[1] = 0x00;
        request[2] = NetworkNo;
        request[3] = PcNo;
        request[4] = RequestDestModuleIo;
        request[5] = 0x03;
        request[6] = RequestDestModuleStation;
        // 监视定时器（小端）
        request[7] = (byte)(CpuWatchdogTimer & 0xFF);
        request[8] = (byte)(CpuWatchdogTimer >> 8);
        // 命令 0x0401 = 批量读（字单位），子命令 0x0000
        request[9] = 0x01;
        request[10] = 0x04;
        request[11] = 0x00;
        request[12] = 0x00;
        // 软元件代码（D=0xA8, W=0xB4, ZR=0xB0）+ 软元件编号（3 字节小端）
        request[13] = deviceCode;
        request[14] = (byte)(deviceNumber & 0xFF);
        request[15] = (byte)((deviceNumber >> 8) & 0xFF);
        request[16] = (byte)((deviceNumber >> 16) & 0xFF);
        // 软元件点数（3 字节小端）
        request[17] = (byte)(wordCount & 0xFF);
        request[18] = (byte)((wordCount >> 8) & 0xFF);
        request[19] = (byte)((wordCount >> 16) & 0xFF);

        await _stream!.WriteAsync(request, cancellationToken);

        // 应答固定头 9 字节 + 结束代码 2 字节 = 11 字节一次读齐
        // （头 9 字节后紧跟结束代码，拆两次读徒增边界风险——此前 header[9] 越界即此因）
        var header = new byte[11];
        await ReadExactlyAsync(header, cancellationToken);

        if (header[0] != FrameSubHeader)
        {
            throw new IOException($"unexpected response sub-header 0x{header[0]:X2}");
        }

        // 结束代码（小端）：0 = 成功；非 0 = PLC 错误（点数范围/软元件不存在等）
        var endCode = (ushort)(header[9] | (header[10] << 8));
        if (endCode != 0)
        {
            throw new IOException($"MC end code 0x{endCode:X4}");
        }

        var dataLength = wordCount * 2;
        var data = new byte[dataLength];
        await ReadExactlyAsync(data, cancellationToken);

        var words = new ushort[wordCount];
        for (var i = 0; i < wordCount; i++)
        {
            words[i] = (ushort)(data[i * 2] | (data[i * 2 + 1] << 8));
        }

        return words;
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream!.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("connection closed by peer");
            }

            offset += read;
        }
    }

    private readonly record struct ParsedAddress(byte DeviceCode, uint DeviceNumber, ushort WordCount, bool SwapWords);

    /// <summary>解析 D100:Float32:CDAB 形式的地址表达式（M1 只支持 Float32）。</summary>
    private static ParsedAddress? ParseAddress(string address)
    {
        var parts = address.Split(':');
        if (parts.Length == 0 || parts[0].Length < 2)
        {
            return null;
        }

        // 软元件前缀可能是 1~2 个字母（D / W / ZR）：最长优先匹配，避免 ZR 被拆成 Z + R100
        byte? deviceCode = null;
        uint deviceNumber = 0;
        foreach (var prefixLength in new[] { 2, 1 })
        {
            if (parts[0].Length <= prefixLength)
            {
                continue;
            }

            var prefix = parts[0][..prefixLength].ToUpperInvariant();
            var numberPart = parts[0][prefixLength..];
            var mapped = prefix switch
            {
                "ZR" => (byte)0xB0,   // 文件寄存器
                "D" => (byte)0xA8,    // 数据寄存器
                "W" => (byte)0xB4,    // 链接寄存器
                _ => (byte)0,
            };

            if (mapped != 0 && uint.TryParse(numberPart, out deviceNumber))
            {
                if (deviceNumber > 0xFFFFFF)
                {
                    return null;   // 软元件编号超 3 字节范围（0xFFFFFF）
                }

                deviceCode = mapped;
                break;
            }
        }

        if (deviceCode is null)
        {
            return null;
        }

        // M1 只支持 Float32；字序默认 CDAB（低字在前）
        var dataType = parts.Length > 1 ? parts[1] : "Float32";
        var byteOrder = parts.Length > 2 ? parts[2] : "CDAB";

        if (!dataType.Equals("Float32", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ParsedAddress(deviceCode.Value, deviceNumber, WordCount: 2,
            SwapWords: byteOrder.Equals("CDAB", StringComparison.OrdinalIgnoreCase));
    }

    private static float ParseFloat32(ushort[] words, bool swapWords)
    {
        var high = swapWords ? words[1] : words[0];
        var low = swapWords ? words[0] : words[1];
        var raw = ((uint)high << 16) | low;
        return BitConverter.Int32BitsToSingle((int)raw);
    }

    // 诊断原因暂入 reason，落库 error 列随 WriteGuard 任务补（故障可追溯）
    private ReadValue Bad(PointRef point, string reason)
    {
        _lastError = reason;
        return new(point.PointId, point.DeviceId, null, QualityCode.Bad, DateTime.UtcNow);
    }

    /// <summary>最近一次 Bad 的诊断原因（健康页/审计用）。</summary>
    public string? LastError => _lastError;

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
        _connectLock.Dispose();
    }
}
