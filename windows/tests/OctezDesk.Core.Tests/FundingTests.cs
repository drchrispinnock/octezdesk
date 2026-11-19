using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OctezDesk.Core.Hosting;
using OctezDesk.Core.Octez;
using OctezDesk.Core.Provisioning;

namespace OctezDesk.Core.Tests;

public class FundingTests
{
    private const string Master = "tz4NdtqZtkNNk5ekoZKmRdb4Z3aFCwKEE22B";
    private const string Rpc = "http://127.0.0.1:8732/chains/main/blocks/head/context";

    [Theory]
    [InlineData(6001, 6000)]
    [InlineData(40000, 6000)]
    [InlineData(80000, 8000)] // 10% when that's more than 6000
    public void Stakes_6000_or_10_percent_whichever_is_higher(int amount, int stake)
        => Assert.Equal(stake, new FundingSettings { Amount = amount }.StakeAmount);

    [Fact]
    public void Amount_must_cover_the_stake_and_fit_the_faucet()
    {
        Assert.NotEmpty(new FundingSettings { Amount = 6000 }.Validate(null));
        Assert.Empty(new FundingSettings { Amount = 6001 }.Validate(6001));
        Assert.NotEmpty(new FundingSettings { Amount = 7000 }.Validate(6001)); // Shadownet's limit
        Assert.Empty(new FundingSettings { Fund = false, Amount = 0 }.Validate(6001));
    }

    [Fact]
    public void Funds_screen_applies_on_testnets_when_setup_creates_a_master_key()
    {
        Assert.True(FundingSettings.AppliesTo(TezosNetwork.Bakingnet, new(BakingKeySetup.MasterKey)));
        Assert.True(FundingSettings.AppliesTo(TezosNetwork.Shadownet, new(BakingKeySetup.MasterAndCompanion)));
        Assert.False(FundingSettings.AppliesTo(TezosNetwork.Mainnet, new(BakingKeySetup.MasterKey)));
        Assert.False(FundingSettings.AppliesTo(TezosNetwork.Bakingnet, new(BakingKeySetup.ConsensusAndCompanion)));
        Assert.False(FundingSettings.AppliesTo(TezosNetwork.Bakingnet, null));
        Assert.Equal("https://faucet.bakingnet.teztnets.com", TezosNetwork.Bakingnet.FaucetUrl);
        Assert.Null(TezosNetwork.Mainnet.FaucetUrl);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0x1f }, 4, true)]
    [InlineData(new byte[] { 0x00, 0x00, 0x1f }, 5, false)]
    [InlineData(new byte[] { 0x00, 0x0f, 0xff }, 3, true)]
    [InlineData(new byte[] { 0x00, 0x10, 0xff }, 3, false)]
    public void Counts_leading_hex_zeros(byte[] hash, int zeros, bool expected)
        => Assert.Equal(expected, Faucet.HasLeadingHexZeros(hash, zeros));

    [Fact]
    public void Solves_a_challenge_the_way_the_faucet_checks_it()
    {
        var (nonce, solution) = Faucet.Solve("abc123", 3);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"abc123:{nonce}")));
        Assert.True(nonce > 0); // the faucet rejects a nonce of 0
        Assert.Equal(hash, solution);
        Assert.StartsWith("000", solution);
    }

    [Fact]
    public async Task Requests_tez_by_solving_each_challenge_in_turn()
    {
        var faucet = new FakeFaucet(challengesEnabled: true, challengesNeeded: 3);
        var hash = await new Faucet(new HttpClient(faucet), new Uri("https://faucet.test")).RequestAsync(Master, 6001, _ => { });

        Assert.Equal("opHash", hash);
        Assert.Equal(3, faucet.Verified);
        Assert.Equal(6001, faucet.RequestedAmount);
    }

    [Fact]
    public async Task Goes_straight_to_verify_when_challenges_are_off()
    {
        var faucet = new FakeFaucet(challengesEnabled: false, challengesNeeded: 0);
        Assert.Equal("opHash", await new Faucet(new HttpClient(faucet), new Uri("https://faucet.test")).RequestAsync(Master, 6001, _ => { }));
        Assert.Equal(6001, faucet.RequestedAmount);
    }

    [Fact]
    public async Task Faucet_errors_are_reported()
    {
        var faucet = new FakeFaucet(challengesEnabled: false, challengesNeeded: 0) { Refuse = "You have already enough ꜩ" };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Faucet(new HttpClient(faucet), new Uri("https://faucet.test")).RequestAsync(Master, 6001, _ => { }));
        Assert.Contains("already enough", ex.Message);
    }

    private static FakeHost Chain(string balance, string staked, bool delegate_ = false) => new FakeHost()
        .On(a => a is ["octez-client", "show", "address", BakingKeyService.MasterAlias], stdout: $"Hash: {Master}\nPublic Key: BLpk1\n")
        .On(a => a is ["wget", .., $"{Rpc}/contracts/{Master}/balance"], stdout: $"\"{balance}\"")
        .On(a => a is ["wget", .., $"{Rpc}/contracts/{Master}/staked_balance"], stdout: $"\"{staked}\"")
        .On(a => a is ["wget", .., $"{Rpc}/delegates/{Master}"], exitCode: delegate_ ? 0 : 8, stdout: delegate_ ? "{\"deactivated\":false}" : "");

    private static MasterAccount Account(FakeHost host) => new(host, new NodeService(host), new OctezClient(host));

    [Theory]
    [InlineData("0", "0", false)]
    [InlineData("6001000000", "0", true)]
    [InlineData("900000", "6000000000", true)] // staked, and fees paid since funding
    [InlineData("100", "0", false)]
    public async Task Funding_is_done_when_the_master_key_holds_the_amount(string balance, string staked, bool done)
    {
        var host = Chain(balance, staked);
        var step = new FundFromFaucetStep(Account(host), Faucet.For(TezosNetwork.Bakingnet), new FundingSettings(), TezosNetwork.Bakingnet);
        Assert.Equal(done, await step.IsDoneAsync(default));
    }

    [Fact]
    public async Task Registers_with_the_companion_key_when_not_yet_a_delegate()
    {
        var host = Chain("6001000000", "0");
        var step = new RegisterDelegateStep(Account(host), withCompanion: true);
        Assert.False(await step.IsDoneAsync(default));
        await step.RunAsync(_ => { }, default);
        Assert.True(host.Ran("octez-client", "bootstrapped"));
        Assert.True(host.Ran("octez-client", "register", "key", "baker", "as", "delegate", "--companion-key", "companion"));

        Assert.True(await new RegisterDelegateStep(Account(Chain("1", "0", delegate_: true)), true).IsDoneAsync(default));
    }

    [Fact]
    public async Task Stakes_what_is_missing()
    {
        var host = Chain("6001000000", "1000000000");
        var step = new StakeStep(Account(host), new FundingSettings());
        Assert.False(await step.IsDoneAsync(default));
        await step.RunAsync(_ => { }, default);
        Assert.True(host.Ran("octez-client", "stake", "5000", "for", "baker"));

        Assert.True(await new StakeStep(Account(Chain("1", "6000000000")), new FundingSettings()).IsDoneAsync(default));
    }

    [Theory]
    [InlineData("1000", 1_000_000_000L)]
    [InlineData("0.5", 500_000L)]
    [InlineData("1,000.000001", 1_000_000_001L)]
    [InlineData(" 6000 ꜩ ", 6_000_000_000L)]
    public void Parses_tez_amounts(string text, long mutez) => Assert.Equal(mutez, TezAmount.TryParse(text));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.0000001")] // below a mutez
    [InlineData("abc")]
    public void Rejects_bad_tez_amounts(string text) => Assert.Null(TezAmount.TryParse(text));

    [Fact]
    public void Formats_tez_for_octez_client()
    {
        Assert.Equal("6000", TezAmount.Format(6_000_000_000));
        Assert.Equal("0.000001", TezAmount.Format(1));
        Assert.Equal("1.5", TezAmount.Format(1_500_000));
    }

    [Fact]
    public void Cannot_unstake_more_than_is_staked()
    {
        Assert.Null(TezAmount.ValidateUnstake("6000", 6_000_760_670));
        Assert.NotNull(TezAmount.ValidateUnstake("6001", 6_000_760_670));
        Assert.NotNull(TezAmount.ValidateUnstake("nope", 6_000_760_670));
    }

    [Fact]
    public async Task Unstakes_an_amount_or_everything_then_finalizes()
    {
        var host = new FakeHost();
        var client = new OctezClient(host);
        await client.UnstakeAsync("baker", 1_500_000);
        await client.UnstakeAsync("baker", null);
        await client.FinalizeUnstakeAsync("baker");
        Assert.True(host.Ran("octez-client", "unstake", "1.5", "for", "baker"));
        Assert.True(host.Ran("octez-client", "unstake", "everything", "for", "baker"));
        Assert.True(host.Ran("octez-client", "finalize", "unstake", "for", "baker"));
    }

    [Fact]
    public void Reads_the_companion_key_from_the_delegate()
    {
        // Shapes from Ushuaianet, 2026-11-04: just registered (pending), and a long-standing baker (active).
        using var pending = JsonDocument.Parse("""
            {"deactivated":false,"companion_key":{"active":null,"pendings":[{"cycle":14106,"pkh":"tz4FpWib","pk":"BLpk1r"}]}}
            """);
        Assert.Equal(new Health.DelegateInfo(null, "tz4FpWib", 14106), Health.DelegateInfo.Parse(pending.RootElement));

        using var active = JsonDocument.Parse("""{"companion_key":{"active":{"pkh":"tz4RwHj1","pk":"BLpk1p"}}}""");
        Assert.Equal(new Health.DelegateInfo("tz4RwHj1", null, null), Health.DelegateInfo.Parse(active.RootElement));

        using var notRegistered = JsonDocument.Parse("""[{"kind":"temporary","id":"proto.025-PsUshuai.delegate.not_registered"}]""");
        Assert.Null(Health.DelegateInfo.Parse(notRegistered.RootElement));
    }

    [Fact]
    public void Plan_funds_and_stakes_last_on_testnets_only()
    {
        var wsl = new Wsl.WslManager();
        var bakingnet = new NodeSettings { Network = TezosNetwork.Bakingnet };
        var steps = ProvisioningPlans.WindowsNode(wsl, new FakeHost(), "C:\\x", bakingnet,
            new(BakingKeySetup.MasterAndCompanion), new DalSettings(), new BakerSettings(), new FundingSettings()).Steps;
        Assert.IsType<FundFromFaucetStep>(steps[^3]);
        Assert.IsType<RegisterDelegateStep>(steps[^2]);
        Assert.IsType<StakeStep>(steps[^1]);

        var noStake = ProvisioningPlans.WindowsNode(wsl, new FakeHost(), "C:\\x", bakingnet,
            new(BakingKeySetup.MasterKey), new DalSettings(), new BakerSettings(), new FundingSettings { Stake = false }).Steps;
        Assert.IsType<FundFromFaucetStep>(noStake[^1]);
        Assert.DoesNotContain(noStake, s => s is StakeStep or RegisterDelegateStep);

        var mainnet = ProvisioningPlans.WindowsNode(wsl, new FakeHost(), "C:\\x", new NodeSettings(),
            new(BakingKeySetup.ConsensusAndCompanion), new DalSettings(), new BakerSettings(), new FundingSettings()).Steps;
        Assert.DoesNotContain(mainnet, s => s is FundFromFaucetStep);
    }

    [Theory]
    [InlineData(BakingKeySetup.MasterKey, false)]
    [InlineData(BakingKeySetup.MasterAndCompanion, false)]
    [InlineData(BakingKeySetup.ConsensusAndCompanion, true)]
    public void Mainnet_only_creates_consensus_and_companion_keys(BakingKeySetup setup, bool allowed)
    {
        Assert.Equal(allowed, BakingKeyChoice.IsAllowedOn(setup, TezosNetwork.Mainnet));
        Assert.True(BakingKeyChoice.IsAllowedOn(setup, TezosNetwork.Shadownet));
        if (!allowed)
            Assert.Throws<ArgumentException>(() => ProvisioningPlans.WindowsNode(new Wsl.WslManager(), new FakeHost(), "C:\\x", new NodeSettings(), new(setup)));
    }

    /// <summary>The faucet backend's protocol: /info, /challenge, then /verify until the tez is sent.</summary>
    private sealed class FakeFaucet(bool challengesEnabled, int challengesNeeded) : HttpMessageHandler
    {
        public int Verified { get; private set; }
        public int? RequestedAmount { get; private set; }
        public string? Refuse { get; init; }
        private string _challenge = "c1";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/info")
                return Json(new { challengesEnabled, minTez = 1, maxTez = 24000, maxBalance = 48000 });

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var b = body.RootElement;
            if (b.TryGetProperty("amount", out var amount)) RequestedAmount = amount.GetInt32();
            if (Refuse is not null) return Json(new { status = "ERROR", message = Refuse }, HttpStatusCode.Forbidden);

            if (path == "/challenge")
                return Json(new { status = "SUCCESS", challenge = _challenge, challengeCounter = 1, challengesNeeded, difficulty = 2 });

            if (!challengesEnabled) return Json(new { status = "SUCCESS", txHash = "opHash" });

            var nonce = b.GetProperty("nonce").GetInt64();
            var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{_challenge}:{nonce}")));
            if (b.GetProperty("solution").GetString() != expected || !expected.StartsWith("00"))
                return Json(new { status = "ERROR", message = "Incorrect solution" }, HttpStatusCode.BadRequest);
            Verified++;
            if (Verified >= challengesNeeded) return Json(new { status = "SUCCESS", txHash = "opHash" });
            _challenge = $"c{Verified + 1}";
            return Json(new { status = "SUCCESS", challenge = _challenge, challengeCounter = Verified + 1, challengesNeeded, difficulty = 2 });
        }

        private static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK)
            => new(code) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }
}
