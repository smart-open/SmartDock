using Platform.Abstractions.Remote;
using Platform.Core.Remote;
using Xunit;

namespace Platform.Tests;

/// <summary>
/// 5.11.4 ② manifest arch 校验：直接断言生产判定 DriverManifest.Requires32BitProcess 与
/// RemoteDriverSupervisor.SelectHostExe（宿主位数选择的真实消费方），不再本地重实现规则。
/// </summary>
public class DriverManifestTests
{
    [Theory]
    [InlineData("x86", true)]      // 32 位卡驱动 → 必须进 32 位子进程
    [InlineData("X86", true)]      // 大小写不敏感（manifest 手写易错）
    [InlineData("x64", false)]
    [InlineData("any", false)]
    [InlineData("bogus", false)]   // 未知 arch 按 x64 处理：宁装错子进程，不把 x86 卡拉进 x64 主进程
    public void Manifest_Requires32BitProcess_MatchesArchRule(string manifestArch, bool expected)
    {
        var manifest = new DriverManifest("driver.test", "plugins/test.dll", manifestArch);

        Assert.Equal(expected, manifest.Requires32BitProcess);
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("any")]
    public void SelectHostExe_X64Manifest_ChoosesX64Host(string arch)
    {
        var manifest = new DriverManifest("driver.test", "plugins/test.dll", arch);

        var exe = RemoteDriverSupervisor.SelectHostExe(manifest, @"hosts\host64.exe", @"hosts\host86.exe");

        Assert.Equal(@"hosts\host64.exe", exe);
    }

    [Fact]
    public void SelectHostExe_X86Manifest_ChoosesX86Host()
    {
        var manifest = new DriverManifest("driver.test", "plugins/test.dll", "x86");

        var exe = RemoteDriverSupervisor.SelectHostExe(manifest, @"hosts\host64.exe", @"hosts\host86.exe");

        Assert.Equal(@"hosts\host86.exe", exe);
    }

    [Fact]
    public void SelectHostExe_X86Manifest_WithoutX86Host_Throws()
    {
        // x86 宿主缺位必须拒绝拉起：装进 x64 进程 = 子进程启动即崩 + 无限重启风暴
        var manifest = new DriverManifest("driver.leisai", "plugins/leisai.dll", "x86");

        Assert.Throws<NotSupportedException>(
            () => RemoteDriverSupervisor.SelectHostExe(manifest, @"hosts\host64.exe", x86ExePath: null));
    }
}
