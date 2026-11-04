namespace OctezDesk.Core.Health;

public enum HealthLevel
{
    /// <summary>Not working.</summary>
    Red,
    /// <summary>Running but degraded (syncing, stuck, no peers).</summary>
    Yellow,
    /// <summary>Running and synced.</summary>
    Green,
}

/// <summary>Raw facts gathered about the node. Null means "could not be determined".</summary>
public sealed record NodeObservation
{
    public bool ServiceActive { get; init; }
    public bool RpcReachable { get; init; }
    public bool? Bootstrapped { get; init; }
    /// <summary>"synced", "unsynced" or "stuck", from <c>/chains/main/is_bootstrapped</c>.</summary>
    public string? SyncState { get; init; }
    public int? PeerCount { get; init; }
    public int? HeadLevel { get; init; }
    public DateTimeOffset? HeadTimestamp { get; init; }
    public string? Version { get; init; }
}

public sealed record NodeHealth(HealthLevel Level, string Summary, NodeObservation Observation);

public static class HealthEvaluator
{
    public static NodeHealth Evaluate(NodeObservation o, DateTimeOffset? now = null)
    {
        if (!o.ServiceActive)
            return new(HealthLevel.Red, "Not running", o);
        if (!o.RpcReachable)
            return new(HealthLevel.Red, "Not responding (it may still be starting)", o);
        if (o.SyncState == "stuck")
            return new(HealthLevel.Yellow, "Stuck — not receiving new blocks", o);
        if (o.Bootstrapped != true || o.SyncState != "synced")
            return new(HealthLevel.Yellow, CatchingUp(o.HeadTimestamp, now ?? DateTimeOffset.UtcNow), o);
        if (o.PeerCount == 0)
            return new(HealthLevel.Yellow, "No peers", o);
        return new(HealthLevel.Green, "Synced", o);
    }

    private static string CatchingUp(DateTimeOffset? head, DateTimeOffset now)
        => head is { } ts && now - ts > TimeSpan.FromMinutes(1)
            ? $"Catching up — {Describe(now - ts)} behind"
            : "Catching up";

    public static string Describe(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => $"{(int)age.TotalSeconds} s",
        { TotalHours: < 1 } => $"{(int)age.TotalMinutes} min",
        { TotalDays: < 2 } => $"{(int)age.TotalHours} h",
        _ => $"{(int)age.TotalDays} days",
    };
}
