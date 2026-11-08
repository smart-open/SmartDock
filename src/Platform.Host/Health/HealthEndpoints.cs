using Platform.Core.Acquisition;

namespace Platform.Host.Health;

/// <summary>H1 健康检查端点（M0 最小 live/ready）。</summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        endpoints.MapGet("/health/ready", (PollingScheduler scheduler) => Results.Ok(new
        {
            status = "ready",
            snapshotPoints = scheduler.GetSnapshot().Count,
        }));
        return endpoints;
    }
}
