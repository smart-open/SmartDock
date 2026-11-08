using Driver.ModbusTcp;
using Platform.Abstractions;
using Xunit;

namespace Platform.Tests;

/// <summary>Modbus TCP 驱动单元测试：地址解析与 Float32 字序（契约测试的种子）。</summary>
public class ModbusAddressTests
{
    [Theory]
    [InlineData("40001:Float32:CDAB", 0, 2, true)]
    [InlineData("40003:Float32:ABCD", 2, 2, false)]
    [InlineData("40001", 0, 2, true)]
    public void ParseAddress_ResolvesRegisterAndWordOrder(string address, ushort expectedRegister, ushort expectedWords, bool expectedSwap)
    {
        var parsed = InvokeParse(address);
        Assert.NotNull(parsed);
        Assert.Equal(expectedRegister, parsed!.Value.RegisterAddress);
        Assert.Equal(expectedWords, parsed.Value.WordCount);
        Assert.Equal(expectedSwap, parsed.Value.SwapWords);
    }

    [Theory]
    [InlineData("39999:Float32:CDAB")]   // 低于保持寄存器基址
    [InlineData("40001:Int16:CDAB")]     // M0 不支持的类型
    [InlineData("abc:Float32")]          // 非数字地址
    public void ParseAddress_RejectsInvalidInput(string address)
    {
        Assert.Null(InvokeParse(address));
    }

    [Fact]
    public void ParseFloat32_CDAB_ReconstructsFloatCorrectly()
    {
        // 123.5f = 0x42F70000；CDAB（低字在前）→ [0x0000, 0x42F7]
        var value = InvokeParseFloat32([0x0000, 0x42F7], swapWords: true);
        Assert.Equal(123.5f, value);
    }

    [Fact]
    public void ParseFloat32_ABCD_ReconstructsFloatCorrectly()
    {
        var value = InvokeParseFloat32([0x42F7, 0x0000], swapWords: false);
        Assert.Equal(123.5f, value);
    }

    [Fact]
    public void Descriptor_IsReadOnly()
    {
        using var driver = new DisposableDriver(new ModbusTcpDriver("127.0.0.1", 5020));
        var descriptor = driver.Describe();
        Assert.Equal("driver.modbus.tcp", descriptor.DriverId);
        Assert.False(descriptor.SupportsWrite);
    }

    /// <summary>同步测试场景的简易包装（驱动契约是 IAsyncDisposable）。</summary>
    private sealed class DisposableDriver(ModbusTcpDriver inner) : IDisposable
    {
        public DriverDescriptor Describe() => inner.Describe();

        public void Dispose()
            => inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static (ushort RegisterAddress, ushort WordCount, bool SwapWords)? InvokeParse(string address)
    {
        var method = typeof(ModbusTcpDriver).GetMethod("ParseAddress",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var runtimeResult = method!.Invoke(null, [address]);
        if (runtimeResult is null)
        {
            return null;
        }

        // ParsedAddress 是驱动内部私有 struct，反射后按属性取值
        var resultType = runtimeResult.GetType();
        return (
            (ushort)resultType.GetProperty("RegisterAddress")!.GetValue(runtimeResult)!,
            (ushort)resultType.GetProperty("WordCount")!.GetValue(runtimeResult)!,
            (bool)resultType.GetProperty("SwapWords")!.GetValue(runtimeResult)!);
    }

    private static float InvokeParseFloat32(ushort[] registers, bool swapWords)
    {
        var method = typeof(ModbusTcpDriver).GetMethod("ParseFloat32",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (float)method!.Invoke(null, [registers, swapWords])!;
    }
}
