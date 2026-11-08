using System.Net.Sockets;
using Platform.Abstractions;

namespace Driver.ModbusTcp;

/// <summary>
/// Modbus TCP 驱动（M0：只读保持寄存器，Float32/CDAB）。
/// 协议逻辑严格保留在本文件内；连接管理/重试/落库归平台（5.12）。
/// </summary>
public sealed class ModbusTcpDriver : IDeviceDriver
{
    // 地址表达式：40001:Float32:CDAB → 保持寄存器 #0 起 2 个字，Float32，字交换
    private const int HoldingRegisterBase = 40001;

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private ushort _transactionId;
    private readonly SemaphoreSlim _gate = new(1, 1);   // 半双工：请求-应答严格串行

    private readonly string _host;
    private readonly int _port;
    private readonly byte _unitId;
    private string? _lastError;

    public ModbusTcpDriver(string host, int port = 502, byte unitId = 1)
    {
        _host = host;
        _port = port;
        _unitId = unitId;
    }

    public DriverDescriptor Describe() =>
        new("driver.modbus.tcp", "Modbus TCP", SupportsWrite: false, MaxBatchPerRequest: 120);

    public async Task<ConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_tcpClient?.Connected == true)
        {
            return new ConnectResult(true, null);
        }

        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(_host, _port, cancellationToken);
            _tcpClient = client;
            _stream = client.GetStream();
            return new ConnectResult(true, null);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return new ConnectResult(false, ex.Message);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _stream?.Dispose();
        _stream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
        await Task.CompletedTask;
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
        if (_stream is null)
        {
            return Bad(point, "not connected");
        }

        var parsed = ParseAddress(point.Address);
        if (parsed is null)
        {
            return Bad(point, $"bad address: {point.Address}");
        }

        try
        {
            var registers = await ReadHoldingRegistersAsync(parsed.Value.RegisterAddress, parsed.Value.WordCount, cancellationToken);
            var value = ParseFloat32(registers, swapWords: parsed.Value.SwapWords);
            return new ReadValue(point.PointId, point.DeviceId, value, QualityCode.Good, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // 通信异常大概率连接已死：主动断开，让下轮轮询走重连（A1 审查项 4）
            await DisconnectAsync(CancellationToken.None);
            return Bad(point, ex.Message);
        }
    }

    private async Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken cancellationToken)
    {
        _transactionId++;
        var request = BuildReadRequest(_transactionId, _unitId, startAddress, count);
        await _stream!.WriteAsync(request, cancellationToken);

        var header = new byte[7];
        await ReadExactlyAsync(header, cancellationToken);

        var responseTransactionId = (ushort)((header[0] << 8) | header[1]);
        if (responseTransactionId != _transactionId)
        {
            throw new IOException($"transaction id mismatch: {responseTransactionId} != {_transactionId}");
        }

        // MBAP: [tid(2) proto(2) length(2) unit(1)]，PDU = FC(1) + byteCount(1) + data
        // 坏响应保护：length 至少含 unitId+FC（A1 审查项 2）
        var pduLength = (header[4] << 8) | header[5];   // 含 unitId
        if (pduLength < 2)
        {
            throw new IOException($"invalid MBAP length {pduLength}");
        }

        var pdu = new byte[pduLength - 1];               // 去掉 unitId
        await ReadExactlyAsync(pdu, cancellationToken);

        var functionCode = pdu[0];

        // Modbus 异常响应：FC | 0x80 + 1 字节异常码
        if ((functionCode & 0x80) != 0)
        {
            throw new IOException($"modbus exception code {pdu[1]:X2}");
        }

        var byteCount = pdu[1];
        if (byteCount != count * 2)
        {
            throw new IOException($"unexpected byte count {byteCount}, expected {count * 2}");
        }

        var registers = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            registers[i] = (ushort)((pdu[2 + i * 2] << 8) | pdu[3 + i * 2]);
        }

        return registers;
    }

    private static byte[] BuildReadRequest(ushort transactionId, byte unitId, ushort startAddress, ushort count) =>
    [
        (byte)(transactionId >> 8), (byte)(transactionId & 0xFF),   // MBAP: transaction id
        0x00, 0x00,                                                  // protocol id
        0x00, 0x06,                                                  // length
        unitId,
        0x03,                                                        // function: read holding registers
        (byte)(startAddress >> 8), (byte)(startAddress & 0xFF),
        (byte)(count >> 8), (byte)(count & 0xFF),
    ];

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

    private readonly record struct ParsedAddress(ushort RegisterAddress, ushort WordCount, bool SwapWords);

    /// <summary>解析 40001:Float32:CDAB 形式的地址表达式。</summary>
    private static ParsedAddress? ParseAddress(string address)
    {
        var parts = address.Split(':');
        if (parts.Length == 0 || !int.TryParse(parts[0], out var modbusAddress))
        {
            return null;
        }

        var registerAddress = modbusAddress - HoldingRegisterBase;

        // ushort 溢出保护：>65535 时 (ushort) 会静默回绕（A1 审查项 1）
        if (registerAddress < 0 || registerAddress > ushort.MaxValue)
        {
            return null;
        }

        // M0 只支持 Float32；字序默认 CDAB（低字在前，现场最常见）
        var dataType = parts.Length > 1 ? parts[1] : "Float32";
        var byteOrder = parts.Length > 2 ? parts[2] : "CDAB";

        if (!dataType.Equals("Float32", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ParsedAddress((ushort)registerAddress, WordCount: 2, SwapWords: byteOrder.Equals("CDAB", StringComparison.OrdinalIgnoreCase));
    }

    private static float ParseFloat32(ushort[] registers, bool swapWords)
    {
        var high = swapWords ? registers[1] : registers[0];
        var low = swapWords ? registers[0] : registers[1];
        var raw = ((uint)high << 16) | low;
        return BitConverter.Int32BitsToSingle((int)raw);
    }

    // 诊断原因暂入 reason，M1 落库时进 error 列（A1 审查项 7：故障可追溯）
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
    }
}
