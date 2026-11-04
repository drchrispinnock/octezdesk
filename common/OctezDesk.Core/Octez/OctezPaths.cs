namespace OctezDesk.Core.Octez;

/// <summary>Locations and names fixed by the tzinit Octez Debian packages.</summary>
public static class OctezPaths
{
    public const string NodeService = "octez-node";
    public const string NodeDefaultsFile = "/etc/default/octez-node";
    /// <summary>Used only if <see cref="NodeDefaultsFile"/> has no DATADIR.</summary>
    public const string DefaultNodeDataDir = "/var/tezos/.tezos-node";
    public const string NodeLogFile = "/var/log/tezos/octez-node.log";
    public const string TezosHome = "/var/tezos";
    /// <summary>OctezDesk's record of the settings it configured the node with (see <see cref="NodeSettingsRecord"/>).</summary>
    public const string OctezDeskNodeSettingsFile = "/var/tezos/.octezdesk-node.json";

    public static readonly string[] NodePackages = ["octez-node", "octez-client"];
}
