// 02 §6.5 组合根装配（2.1：Program.cs 只做装配，DI 写扩展方法）。
// M2-1 只接 Mock 适配器（本地回环"虚拟 MES"，供场景测试断言）；真实适配器随 M2-3。

using Platform.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Core.Mes;
using Platform.Data;

namespace Platform.Host.Mes;

public static class MesServiceCollectionExtensions
{
    public static IServiceCollection AddMesOutbox(this IServiceCollection services)
    {
        services.AddSingleton<MesOutboxStore>(serviceProvider =>
            new MesOutboxStore(serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Platform")!));

        // Worker 依赖接口（测试可换假实现）；Data 实现作为同一单例注册
        services.AddSingleton<IMesOutboxStore>(serviceProvider =>
            serviceProvider.GetRequiredService<MesOutboxStore>());

        // 工单域（M2-2）：报工三写同事务依赖 WorkOrderStore
        services.AddSingleton<WorkOrderStore>(serviceProvider =>
            new WorkOrderStore(serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Platform")!));

        // 对账与兜底（M2-3）：Sending 卡死回置 + 每日 02:00 对账
        services.AddSingleton<MesReconcileStore>(serviceProvider =>
            new MesReconcileStore(serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Platform")!));
        services.AddHostedService<ReconcileScheduler>();

        services.AddSingleton<IMesAdapterProvider, MesAdapterProvider>();
        services.AddOptions<MockMesOptions>().BindConfiguration(MockMesOptions.SectionName);
        services.AddSingleton<IMesAdapter, MockMesAdapter>();

        // REST 适配器（M2-3，6.10 契约口径）：baseUrl 配好即投递；未配 = target 不可用退避
        services.AddOptions<Platform.Core.Mes.RestMesOptions>().BindConfiguration(Platform.Core.Mes.RestMesOptions.SectionName);
        services.AddHttpClient("mes.rest");
        services.AddSingleton<IMesAdapter>(serviceProvider =>
            new Platform.Core.Mes.RestMesAdapter(
                serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("mes.rest"),
                serviceProvider.GetRequiredService<IOptionsMonitor<Platform.Core.Mes.RestMesOptions>>()));

        services.AddHostedService<OutboxWorker>();
        return services;
    }
}

/// <summary>适配器注册表：DI 里所有 IMesAdapter 按 Target 路由（6.5 adapters.Get）。</summary>
public sealed class MesAdapterProvider(IEnumerable<IMesAdapter> adapters) : IMesAdapterProvider
{
    private readonly Dictionary<string, IMesAdapter> _adapters =
        adapters.ToDictionary(adapter => adapter.Target, StringComparer.OrdinalIgnoreCase);

    public IMesAdapter Get(string target)
        => _adapters.TryGetValue(target, out var adapter)
            ? adapter
            : throw new InvalidOperationException($"no MES adapter registered for target '{target}'");
}
