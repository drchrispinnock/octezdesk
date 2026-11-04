using OctezDesk.Core.Hosting;
using OctezDesk.Core.Octez;
using OctezDesk.Core.Provisioning;

namespace OctezDesk.Core.Tests;

public class ProvisionerTests
{
    private sealed class Step(string title, bool done = false, bool fail = false) : IProvisioningStep
    {
        public bool Ran { get; private set; }
        public string Title => title;
        public Task<bool> IsDoneAsync(CancellationToken ct) => Task.FromResult(done);
        public Task RunAsync(Action<string> log, CancellationToken ct)
        {
            Ran = true;
            return fail ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }
    }

    private sealed class SyncProgress(List<StepUpdate> updates) : IProgress<StepUpdate>
    {
        public void Report(StepUpdate value) => updates.Add(value);
    }

    [Fact]
    public async Task Skips_done_steps_and_stops_at_first_failure()
    {
        var a = new Step("a", done: true);
        var b = new Step("b", fail: true);
        var c = new Step("c");
        var updates = new List<StepUpdate>();

        var ok = await new Provisioner([a, b, c]).RunAsync(new SyncProgress(updates), _ => { });

        Assert.False(ok);
        Assert.False(a.Ran);
        Assert.True(b.Ran);
        Assert.False(c.Ran);
        Assert.Contains(updates, u => u.Step == a && u.Status == StepStatus.AlreadyDone);
        Assert.Contains(updates, u => u.Step == b && u.Status == StepStatus.Failed && u.Error == "boom");
    }

    [Fact]
    public async Task Succeeds_when_all_steps_run()
    {
        var ok = await new Provisioner([new Step("a"), new Step("b", done: true)]).RunAsync(new SyncProgress([]), _ => { });
        Assert.True(ok);
    }
}

public class NodeStepTests
{
    [Fact]
    public async Task Install_uses_extrepo_when_available()
    {
        var host = new FakeHost();
        await new InstallOctezStep(host, new NodeService(host)).RunAsync(_ => { }, default);

        Assert.True(host.Ran("extrepo", "enable", "tzinit"));
        Assert.DoesNotContain(host.Calls, c => c.Argv[0] == "sh");
        Assert.True(host.Ran("env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "-y", "-q", "install", "octez-node", "octez-client"));
    }

    [Fact]
    public async Task Install_falls_back_to_manual_repo_with_fingerprint_check()
    {
        var host = new FakeHost().On(a => a[0] == "extrepo", exitCode: 1);
        await new InstallOctezStep(host, new NodeService(host)).RunAsync(_ => { }, default);

        var sh = Assert.Single(host.Calls, c => c.Argv[0] == "sh");
        Assert.Equal(InstallOctezStep.TzinitKeyFingerprint, sh.Argv[^1]);
    }

    [Fact]
    public async Task Configure_runs_config_init_as_tezos_and_records_settings()
    {
        var host = FreshHost();
        var settings = new NodeSettings { Network = TezosNetwork.Shadownet };
        await new ConfigureNodeStep(host, new NodeService(host), settings).RunAsync(_ => { }, default);

        var call = Assert.Single(host.Calls, c => c.Argv[0] == "octez-node");
        Assert.Equal(HostUser.Tezos, call.User);
        Assert.Contains("/n", call.Argv);

        var marker = Assert.Single(host.Calls, c => c.Argv[0] == "sh" && c.Argv[^1] == OctezPaths.OctezDeskNodeSettingsFile);
        Assert.Equal(NodeSettingsRecord.From(settings), NodeSettingsRecord.TryParse(marker.StdIn));
    }

    /// <summary>A host where nothing exists yet: no config, no store, no marker.</summary>
    private static FakeHost FreshHost() => new FakeHost()
        .OnFile(OctezPaths.NodeDefaultsFile, "DATADIR=/n")
        .On(a => a[0] == "test", exitCode: 1)
        .On(a => a is ["systemctl", "is-active", ..], exitCode: 3)
        .On(a => a is ["cat", OctezPaths.OctezDeskNodeSettingsFile], exitCode: 1);

    [Fact]
    public async Task Configure_is_done_only_when_recorded_settings_match()
    {
        var shadownet = new NodeSettings { Network = TezosNetwork.Shadownet };
        var host = FreshHost()
            .On(a => a is ["test", "-e", "/n/config.json"])
            .OnFile(OctezPaths.OctezDeskNodeSettingsFile, NodeSettingsRecord.From(shadownet).ToJson());
        var node = new NodeService(host);

        Assert.True(await new ConfigureNodeStep(host, node, shadownet).IsDoneAsync(default));
        Assert.False(await new ConfigureNodeStep(host, node, new NodeSettings()).IsDoneAsync(default)); // mainnet
    }

    [Fact]
    public async Task Configure_replaces_different_config_when_no_chain_data()
    {
        var host = FreshHost().On(a => a is ["test", "-e", "/n/config.json"]);
        await new ConfigureNodeStep(host, new NodeService(host), new NodeSettings()).RunAsync(_ => { }, default);

        Assert.True(host.Ran("rm", "-f", "/n/config.json"));
        Assert.True(host.Ran("octez-node", "config", "init"));
    }

    [Fact]
    public async Task Configure_cancels_a_running_import_for_the_old_settings()
    {
        var host = FreshHost()
            .On(a => a is ["test", "-e", "/n/config.json"])
            .On(a => a is ["systemctl", "is-active", "--quiet", ImportSnapshotStep.UnitName]);
        await new ConfigureNodeStep(host, new NodeService(host), new NodeSettings()).RunAsync(_ => { }, default);

        Assert.True(host.Ran("systemctl", "stop", ImportSnapshotStep.UnitName));
        Assert.True(host.Ran("rm", "-f", ImportSnapshotStep.DownloadFile));
        Assert.True(host.Ran("octez-node", "config", "init"));
    }

    [Fact]
    public async Task Configure_refuses_to_replace_config_with_chain_data()
    {
        var host = FreshHost()
            .On(a => a is ["test", "-e", "/n/config.json"])
            .On(a => a is ["test", "-d", "/n/store"]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ConfigureNodeStep(host, new NodeService(host), new NodeSettings()).RunAsync(_ => { }, default));
        Assert.False(host.Ran("rm"));
    }

    [Theory]
    [InlineData("""{"rpc":{"listen-addrs":["127.0.0.1:8732"]}}""", true)]
    [InlineData("""{"rpc":{"listen-addrs":["0.0.0.0:8732"]}}""", false)] // earlier builds: the DAL node was blocked by the ACL
    [InlineData("""{"rpc":{"listen-addrs":["127.0.0.1:8732","0.0.0.0:8733"]}}""", false)]
    public async Task Rpc_address_step_is_done_only_for_localhost(string config, bool done)
    {
        var host = FreshHost().OnFile("/n/config.json", config);
        Assert.Equal(done, await new SetNodeRpcAddressStep(host, new NodeService(host), new NodeSettings()).IsDoneAsync(default));
    }

    [Fact]
    public async Task Rpc_address_step_updates_config_and_restarts_a_running_node()
    {
        var host = FreshHost()
            .OnFile("/n/config.json", """{"rpc":{"listen-addrs":["0.0.0.0:8732"]}}""")
            .On(a => a is ["systemctl", "is-active", "--quiet", OctezPaths.NodeService]);
        await new SetNodeRpcAddressStep(host, new NodeService(host), new NodeSettings()).RunAsync(_ => { }, default);

        var call = Assert.Single(host.Calls, c => c.Argv is ["octez-node", "config", "update", ..]);
        Assert.Equal(HostUser.Tezos, call.User);
        Assert.Equal(["octez-node", "config", "update", "--data-dir", "/n", "--net-addr", "0.0.0.0:9732", "--rpc-addr", "127.0.0.1:8732"], call.Argv);
        Assert.True(host.Ran("systemctl", "restart", OctezPaths.NodeService));
    }

    /// <summary>Re-running the wizard on a node with chain data and changing only RPC access or ports must not be a dead end.</summary>
    [Fact]
    public async Task Configure_updates_ports_and_rpc_in_place_for_the_same_chain()
    {
        var before = new NodeSettings { Network = TezosNetwork.Shadownet };
        var after = before with { AllowAllRpc = true, RpcPort = 8733 };
        var host = FreshHost()
            .On(a => a is ["test", "-e", "/n/config.json"])
            .On(a => a is ["test", "-d", "/n/store"])
            .On(a => a is ["systemctl", "is-active", "--quiet", OctezPaths.NodeService])
            .OnFile(OctezPaths.OctezDeskNodeSettingsFile, NodeSettingsRecord.From(before).ToJson());
        await new ConfigureNodeStep(host, new NodeService(host), after).RunAsync(_ => { }, default);

        Assert.True(host.Ran("octez-node", "config", "update", "--data-dir", "/n", "--net-addr", "0.0.0.0:9732",
            "--rpc-addr", "127.0.0.1:8733", "--allow-all-rpc", "127.0.0.1:8733"));
        Assert.False(host.Ran("rm"));
        Assert.False(host.Ran("octez-node", "config", "init"));
        var marker = Assert.Single(host.Calls, c => c.Argv[0] == "sh" && c.Argv[^1] == OctezPaths.OctezDeskNodeSettingsFile);
        Assert.Equal(NodeSettingsRecord.From(after), NodeSettingsRecord.TryParse(marker.StdIn));
        Assert.True(host.Ran("systemctl", "restart", OctezPaths.NodeService));
    }

    [Fact]
    public async Task Configure_refuses_a_different_network_with_chain_data()
    {
        var host = FreshHost()
            .On(a => a is ["test", "-e", "/n/config.json"])
            .On(a => a is ["test", "-d", "/n/store"])
            .OnFile(OctezPaths.OctezDeskNodeSettingsFile, NodeSettingsRecord.From(new NodeSettings { Network = TezosNetwork.Shadownet }).ToJson());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ConfigureNodeStep(host, new NodeService(host), new NodeSettings()).RunAsync(_ => { }, default));
        Assert.False(host.Ran("octez-node"));
    }

    [Theory]
    [InlineData("trixie\n", true)]
    [InlineData("bookworm\n", false)]
    public async Task Release_check_requires_trixie(string codename, bool done)
    {
        var host = new FakeHost().On(a => a[0] == "sh", stdout: codename);
        Assert.Equal(done, await new CheckDebianReleaseStep(host).IsDoneAsync(default));
    }

    [Fact]
    public async Task Configure_failure_throws()
    {
        var host = FreshHost().On(a => a[0] == "octez-node", exitCode: 1);
        await Assert.ThrowsAsync<HostCommandException>(
            () => new ConfigureNodeStep(host, new NodeService(host), new NodeSettings()).RunAsync(_ => { }, default));
    }
}
