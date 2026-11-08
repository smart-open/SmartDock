using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;

namespace Driver.Mc3e;

/// <summary>
/// 插件装配入口（4.8.1）：Host 组合根只调用 AddDriverMc3e() 一行。
/// 插件自带工厂注册——平台不感知具体协议类型。
/// </summary>
public static class DriverServiceCollectionExtensions
{
    public static IServiceCollection AddDriverMc3e(this IServiceCollection services)
        => services.AddSingleton<IDeviceDriverFactory, Mc3eDriverFactory>();
}
