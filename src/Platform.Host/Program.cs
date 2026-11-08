using System.Text.Json.Serialization;
using Platform.Host.Acquisition;
using Platform.Host.Health;
using Platform.Host.Snapshot;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 采集链路装配（Host 专属扩展方法，组合根只留"装什么"不留"怎么装"）
builder.Services.AddAcquisition();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHealthEndpoints();
app.MapSnapshotEndpoints();
app.MapControllers();

app.Run();
