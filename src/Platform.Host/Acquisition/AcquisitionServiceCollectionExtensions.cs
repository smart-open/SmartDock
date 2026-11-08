using Microsoft.Extensions.Options;
using Platform.Abstractions;
using Platform.Core.Acquisition;
using Platform.Data;

namespace Platform.Host.Acquisition;

/// <summary>
/// 采集链路装配（M0：单设备硬编码；M1 换多设备注册表 + 插件工厂）。
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
            return new AcquisitionPipeline(copyWriter.WriteAsync);
        });

        // 单设备采集调度器
        services.AddSingleton<PollingScheduler>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AcquisitionConfig>>().Value;
            var pipeline = serviceProvider.GetRequiredService<AcquisitionPipeline>();
            var logger = serviceProvider.GetRequiredService<ILogger<PollingScheduler>>();

            var driver = new Driver.ModbusTcp.ModbusTcpDriver(
                options.ModbusHost, options.ModbusPort, options.ModbusUnitId);

            var points = Enumerable.Range(0, options.PointCount)
                .Select(index => new PointRef(
                    PointId: index + 1,
                    DeviceId: 1,
                    Address: $"{40001 + index * 2}:Float32:CDAB",
                    DataType: "Float32"))
                .ToArray();

            return new PollingScheduler(
                driver, points, pipeline, TimeSpan.FromSeconds(options.PeriodSeconds), logger);
        });

        services.AddHostedService<AcquisitionHostedService>();
        return services;
    }
}
