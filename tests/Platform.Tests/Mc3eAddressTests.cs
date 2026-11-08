using Driver.Mc3e;
using Platform.Abstractions;
using Xunit;

namespace Platform.Tests;

/// <summary>MC 3E 地址表达式解析边界（D/W/ZR + 范围保护，对齐 ModbusAddressTests 风格）。</summary>
public class Mc3eAddressTests
{
    private static async Task<string> ReadAndGetLastErrorAsync(string address)
    {
        // 未连接时驱动只走地址解析：解析失败返回 Bad("bad address")，不会触网
        await using var driver = new Mc3eDriver("127.0.0.1", 6000);
        var values = await driver.ReadAsync(
            [new PointRef(1, 1, "p1", address, "Float32")]);
        Assert.Equal(QualityCode.Bad, values[0].Quality);
        return driver.LastError!;
    }

    [Theory]
    [InlineData("D100:Float32:CDAB")]
    [InlineData("d100")]          // 小写软元件前缀
    [InlineData("W100:Float32")]  // 链接寄存器
    [InlineData("ZR100:Float32:CDAB")]   // 文件寄存器
    public async Task ValidAddresses_AreNotRejectedByParsing(string address)
    {
        var lastError = await ReadAndGetLastErrorAsync(address);

        // 未连接 → Bad 但原因必须是 not connected（说明地址解析已通过）
        Assert.Equal("not connected", lastError);
    }

    [Theory]
    [InlineData("X100:Float32")]          // 不支持的软元件
    [InlineData("D:Float32")]             // 缺软元件编号
    [InlineData("DFFFFFF+1:Float32")]     // 非 3 字节范围内数字
    [InlineData("D100:Int32")]            // M1 只支持 Float32
    public async Task InvalidAddresses_ReturnBadWithParseReason(string address)
    {
        var lastError = await ReadAndGetLastErrorAsync(address);

        Assert.StartsWith("bad address", lastError);
    }
}
