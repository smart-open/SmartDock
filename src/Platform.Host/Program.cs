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
app.MapControllers();

app.Run();
