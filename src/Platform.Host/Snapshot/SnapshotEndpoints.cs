using Platform.Core.Acquisition;

namespace Platform.Host.Snapshot;

/// <summary>B1 最小快照端点：客户端轮询（SignalR 推送属 M1）。</summary>
public static class SnapshotEndpoints
{
    public static IEndpointRouteBuilder MapSnapshotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/snapshot", (PollingScheduler scheduler) => Results.Ok(scheduler.GetSnapshot()));
        return endpoints;
    }
}
