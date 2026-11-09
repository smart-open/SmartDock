using System.Text.Json.Serialization;
using Driver.ModbusTcp;
using Driver.Mc3e;
using Platform.Host.Auth;
using Platform.Host.Acquisition;
using Platform.Host.Health;
using Platform.Host.Devices;
using Platform.Host.Snapshot;
using Platform.Host.Write;
using Platform.Host.Mes;
using Platform.Host.WorkOrders;
using Platform.Host.Reports;
using Platform.Host.Features;

var builder = WebApplication.CreateBuilder(args);

// DB 连接源：Auth/Devices 端点用（NpgsqlDataSource 线程安全，单例复用连接池）
builder.Services.AddSingleton(sp =>
    new Npgsql.NpgsqlDataSourceBuilder(
        sp.GetRequiredService<IConfiguration>().GetConnectionString("Platform")).Build());

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 插件装配（4.8.1）：每个驱动插件一行；新协议不改组合根其他部分
builder.Services.AddDriverModbusTcp();
builder.Services.AddDriverMc3e();

// 采集链路装配（Host 专属扩展方法，组合根只留"装什么"不留"怎么装"）
builder.Services.AddAcquisition();
builder.Services.AddMesOutbox();
// 报表存储（8.1/8.2）：定义读取 + 编译执行 + 审计留痕，复用全局连接池
builder.Services.AddSingleton<Platform.Core.Reports.ReportStore>();
// 功能开关（3.7）：定义缓存 30s + feature_version 失效；License/插件就绪用默认实现（M3-5 替换）
builder.Services.AddSingleton<Platform.Core.Features.FeatureStore>();
builder.Services.AddSingleton<Platform.Core.Features.IFeatureStore>(sp => sp.GetRequiredService<Platform.Core.Features.FeatureStore>());
builder.Services.AddSingleton<Platform.Abstractions.Features.IFeatureGate, Platform.Core.Features.FeatureGate>();
// 同一实例双注册：IFeatureGate 给业务契约方；具体类给 Planner/端点（需要定义态方法）
builder.Services.AddSingleton(sp => (Platform.Core.Features.FeatureGate)sp.GetRequiredService<Platform.Abstractions.Features.IFeatureGate>());
builder.Services.AddSingleton<Platform.Core.Features.ILicenseService, Platform.Core.Features.DefaultLicenseService>();
builder.Services.AddSingleton<Platform.Core.Features.IPluginReadiness, Platform.Core.Features.DefaultPluginReadiness>();
builder.Services.AddSingleton<Platform.Core.Features.FeatureChangePlanner>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHealthEndpoints();
app.MapSnapshotEndpoints();
app.MapWriteEndpoints();
app.MapAuthEndpoints();
app.MapDeviceEndpoints();
app.MapMesEndpoints();
app.MapWorkOrderEndpoints();
app.MapReportEndpoints();
app.MapFeatureEndpoints();
app.MapControllers();

app.Run();
