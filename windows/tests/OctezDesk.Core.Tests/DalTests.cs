using OctezDesk.Core.Hosting;
using OctezDesk.Core.Octez;
using OctezDesk.Core.Provisioning;

namespace OctezDesk.Core.Tests;

public class DalSettingsTests
{
    private const string Tz1 = "tz1cpry2Vrnx6GmvDTruRhNTYjtVv9hxR1dM";

    [Fact]
    public void Disabled_is_always_valid()
        => Assert.Empty(new DalSettings { Enabled = false, AttesterAddress = "junk" }.Validate(new NodeSettings(), baking: true));

    [Fact]
    public void Baking_without_a_master_key_needs_an_attester()
    {
        Assert.NotEmpty(new DalSettings { Enabled = true }.Validate(new NodeSettings(), baking: true));
        Assert.Empty(new DalSettings { Enabled = true }.Validate(new NodeSettings(), baking: false)); // no attester profile
        Assert.Empty(new DalSettings { Enabled = true, AttesterAddress = Tz1 }.Validate(new NodeSettings(), baking: true));
        Assert.Empty(new DalSettings { Enabled = true, AttestForMasterKey = true }.Validate(new NodeSettings(), baking: true));
    }

    [Theory]
    [InlineData("tz5cpry2Vrnx6GmvDTruRhNTYjtVv9hxR1dM")]
    [InlineData("KT1cpry2Vrnx6GmvDTruRhNTYjtVv9hxR1dM")]
    [InlineData("tz1cpry2Vrnx6GmvDTruRhNTYjtVv9hxR1d")]
    [InlineData("tz1cpry2Vrnx6GmvDTruRhNTYjtVv9hxR1d0")] // 0 isn't base58
    public void Attester_must_be_an_implicit_address(string address)
        => Assert.NotEmpty(new DalSettings { Enabled = true, AttesterAddress = address }.Validate(new NodeSettings(), baking: true));

    [Fact]
    public void Ports_must_not_clash_with_the_node()
        => Assert.NotEmpty(new DalSettings { Enabled = true, RpcPort = 8732 }.Validate(new NodeSettings(), baking: false));

    [Fact]
    public void ConfigInitArgv_points_at_the_local_node_with_attester()
    {
        Assert.Equal(
            ["octez-dal-node", "config", "init", "--data-dir", "/d", "--endpoint", "http://127.0.0.1:8733",
             "--rpc-addr", "127.0.0.1:10732", "--net-addr", "0.0.0.0:11732", "--attester-profiles", Tz1],
            new DalSettings { Enabled = true }.ConfigInitArgv("/d", 8733, Tz1));
        Assert.DoesNotContain("--attester-profiles", new DalSettings { Enabled = true }.ConfigInitArgv("/d", 8732, null));
    }
}

public class DalStepTests
{
    private const string Tz1 = "tz1cpry2Vrnx6GmvDTruRhNTYjtVv9hxR1dM";
    private const string DalConfig = OctezPaths.DefaultDalDataDir + "/config.json";

    private static ConfigureDalNodeStep Step(FakeHost host, DalSettings settings)
        => new(host, new DalNodeService(host), new NodeService(host), new OctezClient(host), settings);

    /// <summary>No DAL config or record yet; DAL node not running.</summary>
    private static FakeHost FreshHost() => new FakeHost()
        .On(a => a[0] == "test", exitCode: 1)
        .On(a => a is ["cat", OctezPaths.OctezDeskDalSettingsFile], exitCode: 1)
        .On(a => a is ["systemctl", "is-active", ..], exitCode: 3);

    [Fact]
    public async Task Configure_attests_for_the_master_key_created_by_setup()
    {
        var host = FreshHost().On(a => a is ["octez-client", "show", "address", BakingKeyService.MasterAlias],
            stdout: $"Hash: {Tz1}\nPublic Key: edpk\n");
        await Step(host, new DalSettings { Enabled = true, AttestForMasterKey = true }).RunAsync(_ => { }, default);

        var init = Assert.Single(host.Calls, c => c.Argv[0] == "octez-dal-node");
        Assert.Equal(HostUser.Tezos, init.User);
        Assert.Equal(["--attester-profiles", Tz1], init.Argv.TakeLast(2));
        var record = Assert.Single(host.Calls, c => c.Argv[0] == "sh" && c.Argv[^1] == OctezPaths.OctezDeskDalSettingsFile);
        Assert.Equal(Tz1, DalSettingsRecord.TryParse(record.StdIn)!.Attester);
    }

    [Fact]
    public async Task Configure_fails_if_the_master_key_is_missing()
    {
        var host = FreshHost().On(a => a is ["octez-client", "show", "address", _], exitCode: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Step(host, new DalSettings { Enabled = true, AttestForMasterKey = true }).RunAsync(_ => { }, default));
    }

    [Fact]
    public async Task Configure_is_done_only_when_the_recorded_attester_matches()
    {
        var host = FreshHost()
            .On(a => a is ["test", "-e", DalConfig])
            .OnFile(OctezPaths.OctezDeskDalSettingsFile, new DalSettingsRecord(Tz1, 10732, 11732, 8732).ToJson());

        Assert.True(await Step(host, new DalSettings { Enabled = true, AttesterAddress = Tz1 }).IsDoneAsync(default));
        Assert.False(await Step(host, new DalSettings { Enabled = true }).IsDoneAsync(default));
        Assert.False(await Step(host, new DalSettings { Enabled = true, AttesterAddress = Tz1, RpcPort = 10733 }).IsDoneAsync(default));
    }

    [Fact]
    public async Task Configure_replaces_an_existing_config_and_restarts_a_running_dal_node()
    {
        var host = FreshHost()
            .On(a => a is ["test", "-e", DalConfig])
            .On(a => a is ["systemctl", "is-active", "--quiet", OctezPaths.DalNodeService]);
        await Step(host, new DalSettings { Enabled = true, AttesterAddress = Tz1 }).RunAsync(_ => { }, default);

        Assert.True(host.Ran("rm", "-f", DalConfig));
        Assert.True(host.Ran("systemctl", "restart", OctezPaths.DalNodeService));
    }

    [Fact]
    public void Plan_sets_up_dal_after_the_node_is_up()
    {
        var steps = ProvisioningPlans.WindowsNode(new Wsl.WslManager(), new FakeHost(), "C:\\x", new NodeSettings(),
            dal: new DalSettings { Enabled = true }).Steps.ToList();
        int At<T>() => steps.FindIndex(s => s is T);

        Assert.True(At<ConfigureNodeStep>() < At<InstallDalNodeStep>());
        Assert.True(At<StartNodeStep>() < At<InstallDalNodeStep>());
        Assert.True(At<StartNodeStep>() < At<StartDalNodeStep>());
        Assert.Equal(-1, At<DisableDalNodeStep>());
    }

    [Fact]
    public void Plan_without_dal_turns_off_an_earlier_one()
    {
        var steps = ProvisioningPlans.WindowsNode(new Wsl.WslManager(), new FakeHost(), "C:\\x", new NodeSettings()).Steps;
        Assert.Contains(steps, s => s is DisableDalNodeStep);
        Assert.DoesNotContain(steps, s => s is InstallDalNodeStep or ConfigureDalNodeStep or StartDalNodeStep);
    }
}

public class ServiceHealthTests
{
    [Theory]
    [InlineData(false, false, false, null)]
    [InlineData(true, false, false, null)]
    [InlineData(true, true, true, Health.HealthLevel.Green)]
    [InlineData(true, true, false, Health.HealthLevel.Red)]
    [InlineData(true, false, true, Health.HealthLevel.Green)] // running though not enabled at boot
    public void Maps_systemd_state_to_a_dot(bool installed, bool enabled, bool active, Health.HealthLevel? expected)
        => Assert.Equal(expected, Health.ServiceHealth.Evaluate(installed, enabled, active, "").Level);

}

public class WizardPrefillTests
{
    [Fact]
    public void Node_record_round_trips_to_settings_without_a_snapshot()
    {
        var s = new NodeSettings { Network = TezosNetwork.Ushuaianet, HistoryMode = HistoryMode.Full, RpcPort = 8733, AllowAllRpc = true };
        Assert.Equal(s with { ImportSnapshot = false }, NodeSettingsRecord.From(s).ToSettings());
        Assert.Null(new NodeSettingsRecord("nonet", "rolling", 9732, 8732, false).ToSettings());
    }

    [Fact]
    public async Task Installed_dal_settings_keep_it_on_when_running()
    {
        var host = new FakeHost()
            .On(a => a is ["dpkg-query", ..], stdout: "install ok installed")
            .OnFile(OctezPaths.OctezDeskDalSettingsFile, new DalSettingsRecord("tz1x", 10733, 11733, 8732).ToJson());
        var dal = await new DalNodeService(host).GetInstalledSettingsAsync();
        Assert.Equal(new DalSettings { Enabled = true, AttesterAddress = "tz1x", RpcPort = 10733, P2PPort = 11733 }, dal);
    }

    [Fact]
    public async Task No_dal_settings_when_not_installed()
    {
        var host = new FakeHost().On(a => a is ["dpkg-query", ..], exitCode: 1);
        Assert.Null(await new DalNodeService(host).GetInstalledSettingsAsync());
    }

    [Fact]
    public async Task Recorded_master_key_choice_keeps_its_type()
    {
        var host = new FakeHost()
            .OnFile(OctezPaths.OctezDeskKeysFile, BakingKeysRecord.From(BakingKeySetup.MasterKey, []).ToJson())
            .On(a => a is ["octez-client", "show", "address", "baker"], stdout: "Hash: tz2abc\nPublic Key: sppk\n");
        Assert.Equal(new BakingKeyChoice(BakingKeySetup.MasterKey, KeyType.Tz2),
            await new BakingKeyService(host, new OctezClient(host)).GetRecordedChoiceAsync());
    }
}
