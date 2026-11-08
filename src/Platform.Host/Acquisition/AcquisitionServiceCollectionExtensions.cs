using Microsoft.Extensions.Options;
using Platform.Abstractions;
using Platform.Core.Acquisition;
using Platform.Data;
using Platform.Host.Write;

namespace Platform.Host.Acquisition;

/// <summary>
/// 采集链路装配（M1：设备台账驱动 + 驱动工厂；M0 为单设备硬编码）。
/// 依赖方向：Host → Core/Data/Abstractions + 插件，符合 2.1 分层规则。
/// </summary>
public static class AcquisitionServiceCollectionExtensions
{
    public static IServiceCollection AddAcquisition(this IServiceCollection services)
    {
        services.AddOptions<AcquisitionConfig>().BindConfiguration(AcquisitionConfig.SectionName);

        // COPY 写入器（2.4：point_history 禁走 EF）
        services.AddSingleton<PointHistoryCopyWriter>(serviceProvider =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Platform");
            return new PointHistoryCopyWriter(connectionString!);
        });

        // 有界队列流水线：设备归属随 ReadValue 走，不再闭包硬编码（R1 修复项 1）
        services.AddSingleton<AcquisitionPipeline>(serviceProvider =>
        {
            var copyWriter = serviceProvider.GetRequiredService<PointHistoryCopyWriter>();
            var logger = serviceProvider.GetRequiredService<ILogger<AcquisitionPipeline>>();
            var options = serviceProvider.GetRequiredService<IOptions<AcquisitionConfig>>().Value;
            var pipeline = new AcquisitionPipeline(
                copyWriter.WriteAsync,
                new BatchOptions
                {
                    QueueCapacity = options.QueueCapacity,
                    FlushInterval = TimeSpan.FromMilliseconds(options.FlushIntervalMs),
                });

            // 落库失败告警进日志（A1 审查项 5：不可丢语义的观测面）
            pipeline.FlushError = ex => logger.LogError(ex, "COPY flush failed, batch retained for retry");
            return pipeline;
        });

        // 设备台账加载器（7.x device/device_point；启动时读一次）
        services.AddSingleton<DeviceRegistryStore>(serviceProvider =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Platform");
            return new DeviceRegistryStore(connectionString!);
        });

        // 写命令落库（5.6 闸 3 载体）
        services.AddSingleton<WriteCommandStore>(serviceProvider =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Platform");
            return new WriteCommandStore(connectionString!);
        });

        // WriteGuard 四道闸（5.6）：权限留痕在 API 层，互锁注入，先落库，回读比对
        services.AddSingleton<WriteGuard>(serviceProvider =>
        {
            var scheduler = serviceProvider.GetRequiredService<PollingScheduler>();
            var writeStore = serviceProvider.GetRequiredService<WriteCommandStore>();
            var logger = serviceProvider.GetRequiredService<ILogger<WriteGuard>>();

            return new WriteGuard(
                driverResolver: scheduler.FindDriver,
                pointResolver: scheduler.FindPoint,
                persistAsync: (pointId, value) => writeStore.InsertPendingAsync(pointId, value, "m1-operator"),
                markRejectedAsync: (commandId, reason) => writeStore.MarkRejectedAsync(commandId, reason),
                markSentAsync: commandId => writeStore.MarkSentAsync(commandId),
                finishAsync: (commandId, error, verified, readBack) =>
                    writeStore.FinishAsync(commandId, verified, error, readBack),
                snapshotLookupAsync: pointId => Task.FromResult(scheduler.GetSnapshotByPoint(pointId)),
                logger: logger);
        });

        // 驱动注册表：实现来自 DI（各插件提供 IDeviceDriverFactory，4.8.1），
        // 核心与 Host 都不硬编码具体协议——新驱动 = 插件项目 + 注册一行。
        services.AddSingleton<DriverRegistry>();

        // 多设备采集调度器：按台账 driver_id 经注册表创建驱动
        services.AddSingleton<PollingScheduler>(serviceProvider =>
        {
            var pipeline = serviceProvider.GetRequiredService<AcquisitionPipeline>();
            var driverRegistry = serviceProvider.GetRequiredService<DriverRegistry>();
            var logger = serviceProvider.GetRequiredService<ILogger<PollingScheduler>>();

            return new PollingScheduler(
                pipeline, driverRegistry.CreateDriver, logger);
        });

        services.AddHostedService<AcquisitionHostedService>();
        return services;
    }
}
