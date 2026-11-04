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
    /// <summary>OctezDesk's record of the baking keys it set up (see <see cref="BakingKeysRecord"/>).</summary>
    public const string OctezDeskKeysFile = "/var/tezos/.octezdesk-keys.json";

    public const string DalPackage = "octez-dal-node";
    public const string DalNodeService = "octez-dal-node";
    public const string DalDefaultsFile = "/etc/default/octez-dal-node";
    /// <summary>Used only if <see cref="DalDefaultsFile"/> has no DATADIR.</summary>
    public const string DefaultDalDataDir = "/var/tezos/.tezos-dal-node";
    public const string DalLogFile = "/var/log/tezos/octez-dal-node.log";
    /// <summary>OctezDesk's record of the settings it configured the DAL node with (see <see cref="DalSettingsRecord"/>).</summary>
    public const string OctezDeskDalSettingsFile = "/var/tezos/.octezdesk-dal.json";

    public const string BakerPackage = "octez-baker";
    public const string BakerService = "octez-baker";
    public const string BakerDefaultsFile = "/etc/default/octez-baker";
    public const string BakerLogFile = "/var/log/tezos/octez-baker.log";

    public static readonly string[] NodePackages = ["octez-node", "octez-client"];
}
