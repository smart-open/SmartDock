using Platform.Abstractions.Remote;
using Xunit;

namespace Platform.Tests;

/// <summary>5.11.4 ② manifest arch 校验：位数不匹配必须 Blocked（x86 卡驱动装不进 x64 主进程）。</summary>
public class DriverManifestTests
{
    [Theory]
    [InlineData("x86", false)]     // 32 位卡驱动 → 必须子进程
    [InlineData("x64", true)]
    [InlineData("any", true)]
    public void Arch_Compatibility_Matrix(string manifestArch, bool expectedLoadableInX64Host)
    {
        var manifest = new DriverManifest("driver.test", "plugins/test.dll", manifestArch);

        // 判定规则与将来 DeviceRuntime 加载器一致：x86 manifest 只能进 x86 宿主进程
        var loadableInX64Host = !manifest.Arch.Equals("x86", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedLoadableInX64Host, loadableInX64Host);
    }
}
