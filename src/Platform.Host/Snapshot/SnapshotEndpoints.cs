using Platform.Core.Acquisition;

namespace Platform.Host.Snapshot;

/// <summary>B1 最小快照端点：客户端轮询（SignalR 推送属 M1）。</summary>
public static class SnapshotEndpoints
{
    public static IEndpointRouteBuilder MapSnapshotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/snapshot", (PollingScheduler scheduler) => Results.Ok(scheduler.GetSnapshot()));
        endpoints.MapGet("/api/snapshot/summary", (PollingScheduler scheduler) =>
        {
            var snapshot = scheduler.GetSnapshot();
            return Results.Ok(new
            {
                total = snapshot.Count,
                good = snapshot.Count(v => v.Quality == Platform.Abstractions.QualityCode.Good),
                bad = snapshot.Count(v => v.Quality == Platform.Abstractions.QualityCode.Bad),
                uncertain = snapshot.Count(v => v.Quality == Platform.Abstractions.QualityCode.Uncertain),
                stale = snapshot.Count(v => v.Quality == Platform.Abstractions.QualityCode.Stale),
            });
        });
        return endpoints;
    }
}
