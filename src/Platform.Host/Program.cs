using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Platform.Abstractions;
using Platform.Core.Acquisition;
using Platform.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AcquisitionConfig>(builder.Configuration.GetSection("Acquisition"));
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// M0：COPY 写入器（2.4 决策：point_history 禁走 EF）
builder.Services.AddSingleton<PointHistoryCopyWriter>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    return new PointHistoryCopyWriter(configuration.GetConnectionString("Platform")!);
});

// M0：采集流水线（有界队列 + 攒批 COPY，2.6A）
builder.Services.AddSingleton<AcquisitionPipeline>(serviceProvider =>
{
    var copyWriter = serviceProvider.GetRequiredService<PointHistoryCopyWriter>();
    return new AcquisitionPipeline(
        (batch, cancellationToken) => copyWriter.WriteAsync(batch, deviceId: 1, cancellationToken));
});

// M0：单设备采集调度器（host 启动即拉起，M1 换为多设备注册表）
builder.Services.AddSingleton<PollingScheduler>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<AcquisitionConfig>>().Value;
    var pipeline = serviceProvider.GetRequiredService<AcquisitionPipeline>();
    var logger = serviceProvider.GetRequiredService<ILogger<PollingScheduler>>();

    var driver = new Driver.ModbusTcp.ModbusTcpDriver(options.ModbusHost, options.ModbusPort, options.ModbusUnitId);

    var points = Enumerable.Range(0, options.PointCount)
        .Select(index => new PointRef(
            PointId: index + 1,
            DeviceId: 1,
            Address: $"{40001 + index * 2}:Float32:CDAB",
            DataType: "Float32"))
        .ToArray();

    return new PollingScheduler(driver, points, pipeline, TimeSpan.FromSeconds(options.PeriodSeconds), logger);
});

builder.Services.AddHostedService<AcquisitionHostedService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// H1 健康检查（最小 live/ready）
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (PollingScheduler scheduler) => Results.Ok(new
{
    status = "ready",
    snapshotPoints = scheduler.GetSnapshot().Count,
}));

// B1 最小快照端点：客户端轮询（SignalR 推送属 M1）
app.MapGet("/api/snapshot", (PollingScheduler scheduler) => Results.Ok(scheduler.GetSnapshot()));

app.MapControllers();

app.Run();

/// <summary>M0 采集配置（appsettings，M1 迁到设备台账）。</summary>
public sealed class AcquisitionConfig
{
    public string ModbusHost { get; init; } = "127.0.0.1";
    public int ModbusPort { get; init; } = 5020;
    public byte ModbusUnitId { get; init; } = 1;
    public int PointCount { get; init; } = 10;
    public int PeriodSeconds { get; init; } = 1;
}

/// <summary>把采集循环挂进 Host 生命周期。</summary>
public sealed class AcquisitionHostedService : IHostedService
{
    private readonly PollingScheduler _scheduler;

    public AcquisitionHostedService(PollingScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _scheduler.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
        => await _scheduler.DisposeAsync();
}
