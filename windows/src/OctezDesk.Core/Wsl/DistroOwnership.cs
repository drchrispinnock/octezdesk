using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Wsl;

public enum DistroOwner
{
    OctezDesk,
    /// <summary>Made by something else: OctezDesk must not touch it.</summary>
    Other,
    /// <summary>Couldn't tell, e.g. the distro didn't start. Treated as OctezDesk's, so a hiccup never makes a second distro.</summary>
    Unknown,
}

/// <summary>
/// Which WSL distro is OctezDesk's. It's called "OctezDesk" unless a distro of that name already
/// existed that OctezDesk didn't make; then it's the first free name of "OctezDesk-2" to
/// "OctezDesk-9". The answer comes from the distros themselves, not a Windows setting, so a
/// reinstalled OctezDesk finds its distro again.
/// </summary>
public static class DistroOwnership
{
    /// <summary>Written by <c>CreateDistroStep</c> into every distro OctezDesk makes.</summary>
    public const string MarkerFile = "/etc/octezdesk-distro";

    /// <summary>
    /// Prints "octezdesk" for a distro OctezDesk made: one with the marker, or, from builds before
    /// the marker, one with OctezDesk's node settings record or its /etc/wsl.conf.
    /// </summary>
    public const string CheckScript =
        "if [ -e /etc/octezdesk-distro ] || [ -e /var/tezos/.octezdesk-node.json ] || " +
        "grep -qs '^# Managed by OctezDesk' /etc/wsl.conf; then echo octezdesk; else echo other; fi";

    public static IReadOnlyList<string> CandidateNames { get; } =
        [WslManager.DefaultDistroName, .. Enumerable.Range(2, 8).Select(i => $"{WslManager.DefaultDistroName}-{i}")];

    public static async Task<DistroOwner> CheckAsync(IOctezHost host, CancellationToken ct = default)
    {
        try
        {
            var r = await host.ShAsync(CheckScript, ct: ct);
            return !r.Success ? DistroOwner.Unknown : r.StdOut.Trim() switch
            {
                "octezdesk" => DistroOwner.OctezDesk,
                "other" => DistroOwner.Other,
                _ => DistroOwner.Unknown,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return DistroOwner.Unknown; }
    }

    /// <summary>
    /// The first candidate name that exists and isn't someone else's; otherwise the first free one.
    /// Only existing candidates are checked, in order, so the usual case is one check of "OctezDesk".
    /// </summary>
    /// <param name="existing">All WSL distros (<c>wsl --list</c>).</param>
    public static async Task<string> ResolveNameAsync(IReadOnlyList<string> existing, Func<string, Task<DistroOwner>> ownerOf)
    {
        bool Exists(string name) => existing.Contains(name, StringComparer.OrdinalIgnoreCase);

        foreach (var name in CandidateNames.Where(Exists))
            if (await ownerOf(name) != DistroOwner.Other) return name;

        return CandidateNames.FirstOrDefault(n => !Exists(n))
            ?? throw new InvalidOperationException(
                $"WSL distros called {CandidateNames[0]} to {CandidateNames[^1]} all exist and weren't made by OctezDesk.");
    }
}
