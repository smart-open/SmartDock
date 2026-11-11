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
using Platform.Host.License;
using Platform.Core.License;
using Platform.Core.Features;
using Platform.Host.Alarms;
using Platform.Host.Admin;
using Platform.Core.Alarms;
using Platform.Host.Identity;
using Platform.Core.Security;

// 单文件发布 + SCM 拉起时系统默认 CWD 是 C:\Windows\System32，
// appsettings/插件目录解析会失败（S19 实测：CWD 不对 → 连接串读为 null）。
// 显式切到 exe 所在目录，控制台/服务两种形态统一。
// 必须在 CreateBuilder 之前：ContentRoot 在 builder 构造时就按当前 CWD 固化。
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);

// 12.2 Windows 服务直装：SCM 拉起时以服务身份运行（工作目录切到 exe 所在目录，
// 否则 appsettings/插件目录解析会落到 C:\Windows\System32）；控制台运行时无影响。
builder.Host.UseWindowsService(options => options.ServiceName = "Platform.Host");

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
// 报警（C1/C3 最小实现，7.3 alarm 表）：连接态派生 + 确认留痕
builder.Services.AddSingleton<AlarmStore>(sp =>
    new AlarmStore(sp.GetRequiredService<IConfiguration>().GetConnectionString("Platform")!));
// 报表存储（8.1/8.2）：定义读取 + 编译执行 + 审计留痕，复用全局连接池
builder.Services.AddSingleton<Platform.Core.Reports.ReportStore>();
// 功能开关（3.7）：定义缓存 30s + feature_version 失效；License/插件就绪用默认实现（M3-5 替换）
builder.Services.AddSingleton<Platform.Core.Features.FeatureStore>();
builder.Services.AddSingleton<Platform.Core.Features.IFeatureStore>(sp => sp.GetRequiredService<Platform.Core.Features.FeatureStore>());
builder.Services.AddSingleton<Platform.Abstractions.Features.IFeatureGate, Platform.Core.Features.FeatureGate>();
// 同一实例双注册：IFeatureGate 给业务契约方；具体类给 Planner/端点（需要定义态方法）
builder.Services.AddSingleton(sp => (Platform.Core.Features.FeatureGate)sp.GetRequiredService<Platform.Abstractions.Features.IFeatureGate>());
// 授权（M3-5，13.6）：文件式 License 验签 + 到期四阶段；公钥随包分发，license.lic 现场导入
builder.Services.AddSingleton<Platform.Core.Features.ILicenseService>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var basePath = sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
    // 相对路径锚定 ContentRoot 而非进程 CWD——SCM 拉起时 CWD 是 System32（12.2 纪律）
    var licensePath = configuration["License:FilePath"] ?? "license.lic";
    if (!Path.IsPathRooted(licensePath))
    {
        licensePath = Path.Combine(basePath, licensePath);
    }
    return new FileLicenseService(
        licensePath,
        configuration["License:PublicKeyXml"] ?? string.Empty);
});
// 具体类双注册：/api/license 端点需要 Phase/Current（实现态），接口给 FeatureGate
builder.Services.AddSingleton(sp => (FileLicenseService)sp.GetRequiredService<Platform.Core.Features.ILicenseService>());
builder.Services.AddSingleton<Platform.Core.Features.IPluginReadiness, Platform.Core.Features.DefaultPluginReadiness>();
builder.Services.AddSingleton<Platform.Core.Features.FeatureChangePlanner>();
// 用户数据范围（9.2）：报表 scopeIds 与 Planner 步骤 1 的平台侧唯一来源
builder.Services.AddSingleton<IUserScopeStore, UserScopeStore>();
// 授权闸（13.6.3）：超限软限 + 降级写闸；数量回调实时查库（存量不追溯）
builder.Services.AddSingleton(sp =>
{
    var licenseService = sp.GetRequiredService<FileLicenseService>();
    var dataSource = sp.GetRequiredService<Npgsql.NpgsqlDataSource>();
    return new LicenseGuardService(
        licenseService,
        currentDeviceCount: () =>
        {
            using var command = dataSource.CreateCommand("SELECT COUNT(*) FROM device");
            return Convert.ToInt32(command.ExecuteScalar());
        },
        currentUserCount: () =>
        {
            using var command = dataSource.CreateCommand("SELECT COUNT(*) FROM sys_user");
            return Convert.ToInt32(command.ExecuteScalar());
        });
});

var app = builder.Build();

// 全局异常兜底：Production 下无此兜底时未处理异常 = 500 空响应且不留日志，
// 现场排障只能靠猜（第 20 轮修复声称合入但补丁未生效，第 22 轮审查实测 /api/features
// 500 空响应发现缺失）。必须注册在管线最前，异常才能被换成结构化错误 + 日志。
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var errorFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var logger = context.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();
    if (errorFeature?.Error is { } error)
    {
        // TraceId 贯穿（02 §10）：日志带 TraceId，响应头回写，现场报障可直接对日志
        logger.LogError(error, "unhandled exception on {Method} {Path} trace={TraceId}",
            context.Request.Method, context.Request.Path, context.TraceIdentifier);
    }
    context.Response.StatusCode = 500;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new { error = "internal server error" });
}));

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
app.MapLicenseEndpoints();
app.MapAlarmEndpoints();
app.MapIdentityEndpoints();
app.MapAdminEndpoints();
app.MapControllers();

app.Run();
