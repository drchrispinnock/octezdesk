namespace OctezDesk.Core.Octez;

public enum HistoryMode
{
    Rolling,
    Full,
    Archive,
}

public static class HistoryModeExtensions
{
    public static string ToCliValue(this HistoryMode mode) => mode switch
    {
        HistoryMode.Rolling => "rolling",
        HistoryMode.Full => "full",
        HistoryMode.Archive => "archive",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}

/// <param name="Id">Short name, also used in snapshot URLs.</param>
/// <param name="ConfigValue">Value passed to <c>octez-node config init --network</c>: a built-in alias or a teztnets URL.</param>
public sealed record TezosNetwork(string Id, string DisplayName, string ConfigValue)
{
    public static readonly TezosNetwork Mainnet = new("mainnet", "Mainnet", "mainnet");
    public static readonly TezosNetwork Shadownet = new("shadownet", "Shadownet", "https://teztnets.com/shadownet");

    public static IReadOnlyList<TezosNetwork> All { get; } = [Mainnet, Shadownet];

    /// <summary>
    /// tzinit snapshot URL. Confirmed for mainnet; the scheme for test networks is assumed
    /// (spec/Windows.md open question 5).
    /// </summary>
    public string SnapshotUrl(HistoryMode mode) => $"https://snapshots.tzinit.org/{Id}/{mode.ToCliValue()}";

    public override string ToString() => DisplayName;
}
