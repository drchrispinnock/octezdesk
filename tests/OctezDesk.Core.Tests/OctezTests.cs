using OctezDesk.Core.Hosting;
using OctezDesk.Core.Octez;

namespace OctezDesk.Core.Tests;

public class NodeSettingsTests
{
    [Fact]
    public void Defaults_are_mainnet_rolling_standard_ports_with_snapshot()
    {
        var s = new NodeSettings();
        Assert.Equal(TezosNetwork.Mainnet, s.Network);
        Assert.Equal(HistoryMode.Rolling, s.HistoryMode);
        Assert.Equal(9732, s.P2PPort);
        Assert.Equal(8732, s.RpcPort);
        Assert.True(s.ImportSnapshot);
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void ConfigInitArgv_maps_wizard_fields()
    {
        var s = new NodeSettings { Network = TezosNetwork.Shadownet, HistoryMode = HistoryMode.Full, P2PPort = 9733, RpcPort = 8733 };
        Assert.Equal(
            ["octez-node", "config", "init", "--data-dir", "/d", "--network", "https://teztnets.com/shadownet",
             "--history-mode", "full", "--net-addr", "[::]:9733", "--rpc-addr", "127.0.0.1:8733"],
            s.ConfigInitArgv("/d"));
    }

    [Fact]
    public void AllowAllRpc_adds_flag_for_rpc_address()
    {
        var argv = new NodeSettings { AllowAllRpc = true }.ConfigInitArgv("/d");
        Assert.Equal(["--allow-all-rpc", "127.0.0.1:8732"], argv.TakeLast(2));
    }

    [Theory]
    [InlineData(80, 8732)]
    [InlineData(9732, 70000)]
    [InlineData(9000, 9000)]
    public void Invalid_ports_are_rejected(int p2p, int rpc)
        => Assert.NotEmpty(new NodeSettings { P2PPort = p2p, RpcPort = rpc }.Validate());

    [Fact]
    public void Archive_cannot_use_snapshot()
        => Assert.NotEmpty(new NodeSettings { HistoryMode = HistoryMode.Archive, ImportSnapshot = true }.Validate());

    [Fact]
    public void Snapshot_url_uses_network_and_mode()
        => Assert.Equal("https://snapshots.tzinit.org/mainnet/rolling", TezosNetwork.Mainnet.SnapshotUrl(HistoryMode.Rolling));
}

public class ShellDefaultsFileTests
{
    [Fact]
    public void Parses_quotes_comments_and_export()
    {
        var d = ShellDefaultsFile.Parse("""
            # comment
            DATADIR=/var/tezos/.tezos-node
            RUNTIME_OPTS="--a --b"
            export X='y z'
            Y=plain # trailing
            not a line
            """);
        Assert.Equal("/var/tezos/.tezos-node", d["DATADIR"]);
        Assert.Equal("--a --b", d["RUNTIME_OPTS"]);
        Assert.Equal("y z", d["X"]);
        Assert.Equal("plain", d["Y"]);
        Assert.Equal(4, d.Count);
    }
}

public class NodeConfigFileTests
{
    [Theory]
    [InlineData("""{"rpc":{"listen-addrs":["127.0.0.1:8733"]}}""", 8733)]
    [InlineData("""{"rpc":{"listen-addrs":["[::1]:18732"]}}""", 18732)]
    [InlineData("""{"rpc":{"listen-addr":"localhost:9999"}}""", 9999)]
    public void Reads_rpc_port(string json, int expected) => Assert.Equal(expected, NodeConfigFile.TryGetRpcPort(json));

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"rpc":{"listen-addrs":[]}}""")]
    [InlineData("not json")]
    public void Missing_rpc_port_is_null(string json) => Assert.Null(NodeConfigFile.TryGetRpcPort(json));
}

public class NodeServiceTests
{
    [Fact]
    public async Task Data_dir_comes_from_defaults_file()
    {
        var host = new FakeHost().OnFile(OctezPaths.NodeDefaultsFile, "DATADIR=\"/srv/node\"\n");
        Assert.Equal("/srv/node/config.json", await new NodeService(host).GetConfigFilePathAsync());
    }

    [Fact]
    public async Task Data_dir_falls_back_to_package_default()
    {
        var host = new FakeHost().On(a => a[0] == "cat", exitCode: 1);
        Assert.Equal(OctezPaths.DefaultNodeDataDir, await new NodeService(host).GetDataDirAsync());
    }

    [Fact]
    public async Task Rpc_port_read_from_node_config()
    {
        var host = new FakeHost()
            .OnFile(OctezPaths.NodeDefaultsFile, "DATADIR=/n")
            .OnFile("/n/config.json", """{"rpc":{"listen-addrs":["127.0.0.1:18732"]}}""");
        Assert.Equal(18732, await new NodeService(host).GetRpcPortAsync());
    }

    [Fact]
    public async Task Installed_requires_dpkg_install_ok_installed()
    {
        var installed = new FakeHost().On(a => a[0] == "dpkg-query", stdout: "install ok installed");
        var removed = new FakeHost().On(a => a[0] == "dpkg-query", stdout: "deinstall ok config-files");
        Assert.True(await new NodeService(installed).IsInstalledAsync());
        Assert.False(await new NodeService(removed).IsInstalledAsync());
    }
}
