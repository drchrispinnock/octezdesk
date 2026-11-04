using OctezDesk.Core.Health;
using OctezDesk.Core.Octez;
using OctezDesk.Core.Provisioning;

namespace OctezDesk.Core.Tests;

public class BakerTests
{
    private const string Expected = """
        # Managed by OctezDesk. Changes here are replaced when you run its setup wizard.
        DATADIR=/var/tezos/.tezos-node
        ENDPOINT="http://127.0.0.1:8732"
        CLIENTDIR=/var/tezos/.tezos-client
        KEYS="consensus companion"
        DAL_OPTIONS="--dal-node http://127.0.0.1:10732"
        LQ_VOTE="off"
        RUNTIME_OPTS=""

        """;

    [Theory]
    [InlineData(LiquidityBakingVote.Pass, "pass")]
    [InlineData(LiquidityBakingVote.On, "on")]
    [InlineData(LiquidityBakingVote.Off, "off")]
    public void Votes_map_to_cli_values(LiquidityBakingVote vote, string cli)
    {
        Assert.Equal(cli, vote.ToCliValue());
        Assert.Equal(vote, LiquidityBakingVoteExtensions.FromCliValue(cli));
    }

    [Fact]
    public void Defaults_file_has_keys_dal_and_vote()
        => Assert.Equal(Expected.ReplaceLineEndings("\n"), BakerService.DefaultsFile(
            "/var/tezos/.tezos-node", ["consensus", "companion"], 10732, new BakerSettings(LiquidityBakingVote.Off), 8732).ReplaceLineEndings("\n"));

    [Fact]
    public void Defaults_file_without_dal()
        => Assert.Contains("DAL_OPTIONS=\"--without-dal\"",
            BakerService.DefaultsFile("/d", ["baker"], null, new BakerSettings(), 8732));

    private static FakeHost HostWithKeys(BakingKeySetup setup, params (KeyRole Role, string Alias)[] keys) => new FakeHost()
        .OnFile(OctezPaths.OctezDeskKeysFile,
            new BakingKeysRecord(setup.ToString(), keys.ToDictionary(k => k.Role.ToString(), k => k.Alias)).ToJson())
        .On(a => a is ["systemctl", "is-active", ..], exitCode: 3);

    private static ConfigureBakerStep Step(FakeHost host, DalSettings? dal, LiquidityBakingVote vote = LiquidityBakingVote.Pass)
        => new(host, new BakerService(host), new NodeService(host), new BakingKeyService(host, new OctezClient(host)), dal, new BakerSettings(vote));

    [Fact]
    public async Task Configure_puts_the_consensus_key_before_the_companion_key()
    {
        // Recorded in the "wrong" order; the baker gets consensus first.
        var host = HostWithKeys(BakingKeySetup.ConsensusAndCompanion, (KeyRole.Companion, "companion"), (KeyRole.Consensus, "consensus"));
        await Step(host, new DalSettings { Enabled = true }, LiquidityBakingVote.Off).RunAsync(_ => { }, default);

        var write = Assert.Single(host.Calls, c => c.Argv[0] == "sh" && c.Argv[^1] == OctezPaths.BakerDefaultsFile);
        Assert.Equal(Expected.ReplaceLineEndings("\n"), write.StdIn!.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Configure_is_done_when_the_file_matches()
    {
        var host = HostWithKeys(BakingKeySetup.MasterKey, (KeyRole.Master, "baker"))
            .OnFile(OctezPaths.BakerDefaultsFile, BakerService.DefaultsFile("/var/tezos/.tezos-node", ["baker"], null, new BakerSettings(), 8732));

        Assert.True(await Step(host, dal: null).IsDoneAsync(default));
        Assert.False(await Step(host, dal: null, LiquidityBakingVote.On).IsDoneAsync(default));
        Assert.False(await Step(host, new DalSettings { Enabled = true }).IsDoneAsync(default));
    }

    [Fact]
    public async Task Configure_fails_without_recorded_keys()
    {
        var host = new FakeHost().On(a => a is ["cat", OctezPaths.OctezDeskKeysFile], exitCode: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Step(host, null).RunAsync(_ => { }, default));
    }

    [Fact]
    public void Plan_runs_the_baker_only_when_baking_and_after_the_dal_node()
    {
        var steps = ProvisioningPlans.WindowsNode(new Wsl.WslManager(), new FakeHost(), "C:\\x", new NodeSettings { Network = TezosNetwork.Bakingnet },
            new BakingKeyChoice(BakingKeySetup.MasterKey), new DalSettings { Enabled = true }, new BakerSettings()).Steps.ToList();
        int At<T>() => steps.FindIndex(s => s is T);
        Assert.True(At<GenerateBakingKeysStep>() < At<ConfigureBakerStep>());
        Assert.True(At<StartNodeStep>() < At<GenerateBakingKeysStep>()); // keys need the node up
        Assert.True(At<StartDalNodeStep>() < At<StartBakerStep>());

        var notBaking = ProvisioningPlans.WindowsNode(new Wsl.WslManager(), new FakeHost(), "C:\\x", new NodeSettings()).Steps;
        Assert.Contains(notBaking, s => s is DisableBakerStep);
        Assert.DoesNotContain(notBaking, s => s is InstallBakerStep or ConfigureBakerStep or StartBakerStep);
    }

    [Fact]
    public void Dashboard_details_summarise_the_defaults_file()
        => Assert.Equal("Keys consensus, companion · LB vote off · with DAL", BakerMonitor.Describe(Expected));

    [Fact]
    public async Task Installed_vote_is_read_back_for_the_wizard()
    {
        var host = new FakeHost().OnFile(OctezPaths.BakerDefaultsFile, Expected);
        Assert.Equal(new BakerSettings(LiquidityBakingVote.Off), await new BakerService(host).GetInstalledSettingsAsync());
    }
}

public class LinuxTextTests
{
    /// <summary>Files written into the distro must use LF: systemd would read "pass\r" from a CRLF EnvironmentFile.</summary>
    [Fact]
    public void Baker_defaults_file_has_unix_line_endings()
        => Assert.DoesNotContain('\r', BakerService.DefaultsFile("/d", ["baker"], 10732, new BakerSettings(), 8732));
}
