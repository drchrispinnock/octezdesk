using OctezDesk.Core.Hosting;
using OctezDesk.Core.Octez;

namespace OctezDesk.Core.Tests;

public class BakingKeysTests
{
    private const string Warning = "Warning:\n\n   This is NOT the Tezos Mainnet.\n\n";

    /// <summary>
    /// A wallet in the distro: <c>show address</c> succeeds only for aliases that exist,
    /// <c>gen keys</c> adds one, using the address prefix for the requested --sig.
    /// </summary>
    private sealed class Wallet
    {
        public Dictionary<string, string> Addresses { get; } = [];
        public FakeHost Host { get; } = new();

        public FakeHost Build()
        {
            Host.On(a => a is ["octez-client", "show", "address", _], () =>
            {
                var alias = Host.Calls[^1].Argv[3];
                return Addresses.TryGetValue(alias, out var addr)
                    ? new CommandResult(0, $"{Warning}Hash: {addr}\nPublic Key: pk_{alias}\n", "")
                    : new CommandResult(1, Warning, $"Error:\n  no public key hash alias named {alias}\n");
            });
            Host.On(a => a is ["octez-client", "gen", "keys", _, "--sig", _], () =>
            {
                var argv = Host.Calls[^1].Argv;
                var prefix = argv[5] switch { "ed25519" => "tz1", "secp256k1" => "tz2", "p256" => "tz3", _ => "tz4" };
                Addresses[argv[3]] = prefix + "Generated" + argv[3];
                return new CommandResult(0, Warning, "");
            });
            Host.On(a => a is ["octez-client", "create", "bls", "proof", "for", _], () =>
                new CommandResult(0, $"{Warning}BLsigProofOf{Host.Calls[^1].Argv[5]}\n", ""));
            return Host;
        }
    }

    private static BakingKeyService Service(FakeHost host) => new(host, new OctezClient(host));

    [Theory]
    [InlineData(KeyType.Tz1, "ed25519")]
    [InlineData(KeyType.Tz2, "secp256k1")]
    [InlineData(KeyType.Tz3, "p256")]
    [InlineData(KeyType.Tz4, "bls")]
    public void Key_types_map_to_octez_sig(KeyType type, string sig) => Assert.Equal(sig, type.ToSig());

    [Fact]
    public void Plans_match_the_three_setups()
    {
        Assert.Equal([(KeyRole.Master, "baker", KeyType.Tz2)], BakingKeyService.Plan(BakingKeySetup.MasterKey, KeyType.Tz2));
        Assert.Equal(
            [(KeyRole.Master, "baker", KeyType.Tz4), (KeyRole.Companion, "companion", KeyType.Tz4)],
            BakingKeyService.Plan(BakingKeySetup.MasterAndCompanion, KeyType.Tz1)); // master type ignored: must be tz4
        Assert.Equal(
            [(KeyRole.Consensus, "consensus", KeyType.Tz4), (KeyRole.Companion, "companion", KeyType.Tz4)],
            BakingKeyService.Plan(BakingKeySetup.ConsensusAndCompanion, KeyType.Tz1));
    }

    [Fact]
    public async Task Master_key_of_a_non_bls_type_has_no_proof()
    {
        var host = new Wallet().Build();
        var keys = await Service(host).CreateAsync(new BakingKeyChoice(BakingKeySetup.MasterKey, KeyType.Tz1));

        var key = Assert.Single(keys);
        Assert.Equal(("baker", "tz1Generatedbaker", "pk_baker"), (key.Alias, key.Address, key.PublicKey));
        Assert.Null(key.ProofOfPossession);
        Assert.False(host.Ran("octez-client", "create", "bls", "proof", "for"));
        Assert.All(host.Calls.Where(c => c.Argv[0] == "octez-client"), c => Assert.Equal(HostUser.Tezos, c.User));
    }

    [Fact]
    public async Task Consensus_and_companion_keys_come_with_proofs_of_possession()
    {
        var host = new Wallet().Build();
        var keys = await Service(host).CreateAsync(new BakingKeyChoice(BakingKeySetup.ConsensusAndCompanion, KeyType.Tz1));

        Assert.Equal([KeyRole.Consensus, KeyRole.Companion], keys.Select(k => k.Role));
        Assert.Equal(["tz4Generatedconsensus", "tz4Generatedcompanion"], keys.Select(k => k.Address));
        Assert.Equal(["BLsigProofOfconsensus", "BLsigProofOfcompanion"], keys.Select(k => k.ProofOfPossession));
        Assert.True(host.Ran("octez-client", "gen", "keys", "consensus", "--sig", "bls"));

        var record = Assert.Single(host.Calls, c => c.Argv[0] == "sh" && c.Argv[^1] == OctezPaths.OctezDeskKeysFile);
        Assert.Equal(
            new Dictionary<string, string> { ["Consensus"] = "consensus", ["Companion"] = "companion" },
            BakingKeysRecord.TryParse(record.StdIn)!.Aliases);
    }

    [Fact]
    public async Task Existing_key_of_the_right_type_is_reused_not_regenerated()
    {
        var wallet = new Wallet();
        wallet.Addresses["baker"] = "tz4Existing";
        var host = wallet.Build();

        var keys = await Service(host).CreateAsync(new BakingKeyChoice(BakingKeySetup.MasterAndCompanion, KeyType.Tz4));

        Assert.True(keys[0].AlreadyExisted);
        Assert.Equal("tz4Existing", keys[0].Address);
        Assert.False(host.Ran("octez-client", "gen", "keys", "baker"));
        Assert.True(host.Ran("octez-client", "gen", "keys", "companion"));
        Assert.Equal("BLsigProofOfbaker", keys[0].ProofOfPossession);
    }

    [Fact]
    public async Task Existing_key_of_another_type_fails_before_generating_anything()
    {
        var wallet = new Wallet();
        wallet.Addresses["companion"] = "tz1Existing";
        var host = wallet.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(host).CreateAsync(new BakingKeyChoice(BakingKeySetup.MasterAndCompanion, KeyType.Tz4)));

        Assert.Contains("never overwrites", ex.Message);
        Assert.False(host.Ran("octez-client", "gen", "keys"));
    }

    [Fact]
    public async Task Show_address_parses_hash_and_public_key_around_the_testnet_warning()
    {
        var host = new FakeHost().On(a => a is ["octez-client", "show", "address", "k"],
            stdout: $"{Warning}Hash: tz4NdtqZtkNNk5ekoZKmRdb4Z3aFCwKEE22B\nPublic Key: BLpk1mPUE5mn\n");
        Assert.Equal(("tz4NdtqZtkNNk5ekoZKmRdb4Z3aFCwKEE22B", "BLpk1mPUE5mn"), await new OctezClient(host).ShowAddressAsync("k"));
    }

    private static string Record(BakingKeySetup setup, params (KeyRole Role, string Alias)[] keys)
        => new BakingKeysRecord(setup.ToString(), keys.ToDictionary(k => k.Role.ToString(), k => k.Alias)).ToJson();

    [Fact]
    public async Task Step_is_done_only_when_recorded_setup_matches_and_keys_have_the_right_type()
    {
        var wallet = new Wallet();
        wallet.Addresses["baker"] = "tz1Existing";
        var host = wallet.Build().OnFile(OctezPaths.OctezDeskKeysFile, Record(BakingKeySetup.MasterKey, (KeyRole.Master, "baker")));
        var service = Service(host);

        Assert.True(await new Provisioning.GenerateBakingKeysStep(service, new(BakingKeySetup.MasterKey, KeyType.Tz1), new NodeService(host)).IsDoneAsync(default));
        Assert.False(await new Provisioning.GenerateBakingKeysStep(service, new(BakingKeySetup.MasterKey, KeyType.Tz4), new NodeService(host)).IsDoneAsync(default));
        Assert.False(await new Provisioning.GenerateBakingKeysStep(service, new(BakingKeySetup.MasterAndCompanion), new NodeService(host)).IsDoneAsync(default));
    }

    [Fact]
    public async Task Step_is_not_done_without_a_record()
    {
        var host = new Wallet().Build().On(a => a is ["cat", OctezPaths.OctezDeskKeysFile], exitCode: 1);
        Assert.False(await new Provisioning.GenerateBakingKeysStep(Service(host), new(BakingKeySetup.MasterKey), new NodeService(host)).IsDoneAsync(default));
    }

    [Fact]
    public async Task GetKeys_reads_recorded_keys_back_with_proofs_for_tz4_only()
    {
        var wallet = new Wallet();
        wallet.Addresses["consensus"] = "tz4C";
        wallet.Addresses["companion"] = "tz4D";
        var host = wallet.Build().OnFile(OctezPaths.OctezDeskKeysFile,
            Record(BakingKeySetup.ConsensusAndCompanion, (KeyRole.Companion, "companion"), (KeyRole.Consensus, "consensus")));

        var keys = await Service(host).GetKeysAsync();

        Assert.Equal([KeyRole.Consensus, KeyRole.Companion], keys.Select(k => k.Role));
        Assert.Equal(["BLsigProofOfconsensus", "BLsigProofOfcompanion"], keys.Select(k => k.ProofOfPossession));
        Assert.False(host.Ran("octez-client", "gen", "keys"));
    }

    [Fact]
    public void Plan_includes_the_keys_step_only_when_baking()
    {
        var wsl = new Wsl.WslManager();
        var host = new FakeHost();
        Assert.DoesNotContain(Provisioning.ProvisioningPlans.WindowsNode(wsl, host, "C:\\x", new NodeSettings()).Steps,
            s => s is Provisioning.GenerateBakingKeysStep);
        var steps = Provisioning.ProvisioningPlans.WindowsNode(wsl, host, "C:\\x", new NodeSettings { Network = TezosNetwork.Shadownet }, new(BakingKeySetup.MasterKey)).Steps;
        var keys = steps.ToList().FindIndex(s => s is Provisioning.GenerateBakingKeysStep);
        Assert.True(keys > steps.ToList().FindIndex(s => s is Provisioning.StartNodeStep)); // octez-client is slow without a node
    }
}
