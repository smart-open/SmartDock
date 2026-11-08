using Platform.Abstractions;
using Platform.Core.Acquisition;
using Xunit;

namespace Platform.Tests;

/// <summary>4.8.1 驱动注册表：按 driver_id 路由到插件工厂，未知驱动给出可诊断错误。</summary>
public class DriverRegistryTests
{
    private sealed class StubDriver : IDeviceDriver
    {
        public DriverDescriptor Describe() => new("driver.stub", "Stub", false, 1);
        public Task<ConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ConnectResult(true, null));
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ReadValue>> ReadAsync(
            IReadOnlyList<PointRef> points, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReadValue>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubFactory : IDeviceDriverFactory
    {
        public string DriverId => "driver.stub";
        public IDeviceDriver Create(DeviceConfig device) => new StubDriver();
    }

    [Fact]
    public void CreateDriver_UnknownDriverId_ThrowsWithRegisteredIds()
    {
        var registry = new DriverRegistry([new StubFactory()]);
        var device = new DeviceConfig(1, "DEV-001", "driver.missing", "{}", PollMs: 1000);

        var exception = Assert.Throws<NotSupportedException>(() => registry.CreateDriver(device));

        // 错误信息必须包含已注册驱动列表，现场排障能直接看出该装哪个插件
        Assert.Contains("driver.missing", exception.Message);
        Assert.Contains("driver.stub", exception.Message);
        Assert.Contains("DEV-001", exception.Message);
    }

    [Fact]
    public async Task CreateDriver_KnownDriverId_RoutesToFactory()
    {
        var registry = new DriverRegistry([new StubFactory()]);
        var device = new DeviceConfig(1, "DEV-001", "driver.stub", "{}", PollMs: 1000);

        await using var driver = registry.CreateDriver(device);

        Assert.Equal("driver.stub", driver.Describe().DriverId);
    }

    [Fact]
    public void Constructor_DuplicateDriverId_Throws()
    {
        // 同一 driver_id 注册两次属于装配错误，必须在启动时暴露而不是运行时随机选一个
        Assert.Throws<ArgumentException>(() => new DriverRegistry([new StubFactory(), new StubFactory()]));
    }
}
