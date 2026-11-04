using OctezDesk.Core.Octez;

namespace OctezDesk.Core.Health;

/// <summary>A systemd service's state for a dashboard row (DAL node, baker). <see cref="Level"/> null: not set up (grey dot).</summary>
public sealed record ServiceHealth(HealthLevel? Level, string Summary, string Details)
{
    public const string NotSetUpHint = "To add one, use ☰ → Setup wizard…";

    /// <summary>Pure: maps systemd state to a dot and text. <paramref name="details"/> describes the configuration.</summary>
    public static ServiceHealth Evaluate(bool installed, bool enabled, bool active, string details)
    {
        if (!installed || (!enabled && !active)) return new(null, "Not set up", NotSetUpHint);
        return active
            ? new(HealthLevel.Green, "Running", details)
            : new(HealthLevel.Red, "Not running", details);
    }
}

public sealed class DalMonitor(DalNodeService dal)
{
    public async Task<ServiceHealth> CheckAsync(CancellationToken ct = default)
    {
        if (!await dal.IsInstalledAsync(ct)) return ServiceHealth.Evaluate(false, false, false, "");
        var s = await dal.GetConfiguredSettingsAsync(ct);
        var details = s is null ? ""
            : (s.Attester is { } a ? $"Attester {a}" : "No attester profile") + $" · RPC {s.RpcPort} · P2P {s.P2PPort}";
        return ServiceHealth.Evaluate(true, await dal.IsEnabledAsync(ct), await dal.IsActiveAsync(ct), details);
    }
}

public sealed class BakerMonitor(BakerService baker)
{
    public async Task<ServiceHealth> CheckAsync(CancellationToken ct = default)
    {
        if (!await baker.IsInstalledAsync(ct)) return ServiceHealth.Evaluate(false, false, false, "");
        return ServiceHealth.Evaluate(true, await baker.IsEnabledAsync(ct), await baker.IsActiveAsync(ct),
            Describe(await baker.ReadDefaultsFileAsync(ct)));
    }

    /// <summary>Pure: "Keys consensus, companion · LB vote pass · with DAL" from the defaults file.</summary>
    public static string Describe(string? defaultsFile)
    {
        if (defaultsFile is null) return "";
        var d = ShellDefaultsFile.Parse(defaultsFile);
        var parts = new List<string>();
        if (d.GetValueOrDefault("KEYS") is { Length: > 0 } keys) parts.Add("Keys " + string.Join(", ", keys.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        if (d.GetValueOrDefault("LQ_VOTE") is { Length: > 0 } vote) parts.Add("LB vote " + vote);
        parts.Add(d.GetValueOrDefault("DAL_OPTIONS")?.Contains("--dal-node") == true ? "with DAL" : "without DAL");
        return string.Join(" · ", parts);
    }
}
