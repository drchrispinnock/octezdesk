using OctezDesk.Core.Hosting;
using OctezDesk.Core.Octez;

namespace OctezDesk.Core.Tests;

public class RemoteSignerTests
{
    // Test vectors from Signet's RemoteSignerTests.
    private const string Alice = "tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG";
    private const string AliceHash = "00120136f4f9c3d29772aa2b3663e3c24acfc735e1";
    private const string Bob = "tz4Euk9qYr7b43PC8iyufuK4ycrmhiQ7NYJY";
    private const string Carol = "tz4HVR6aty9KwsQFHh81C1G7gBdhxT8kuytm";

    private static readonly RemoteSigner Tcp = new(SignerTransport.Tcp, "192.168.1.20", 7732);

    [Theory]
    [InlineData(SignerTransport.Tcp, 7732)]
    [InlineData(SignerTransport.Http, 6732)]
    [InlineData(SignerTransport.Https, 443)]
    public void Default_ports_are_octez_signers(SignerTransport transport, int port) => Assert.Equal(port, transport.DefaultPort());

    [Fact]
    public void Locators_are_what_octez_client_imports()
    {
        Assert.Equal("tcp://192.168.1.20:7732", Tcp.Uri);
        Assert.Equal($"tcp://192.168.1.20:7732/{Bob}", Tcp.KeyUri(Bob));
        Assert.Equal("https://signer.example:443", new RemoteSigner(SignerTransport.Https, "signer.example", 443).Uri);
    }

    [Theory]
    [InlineData("tcp://192.168.1.20:7732")]
    [InlineData("http://localhost:6732")]
    [InlineData("https://signer.example:443")]
    [InlineData("https://signer.example:8443")]
    public void Uri_round_trips(string uri) => Assert.Equal(uri, RemoteSigner.TryParse(uri)!.Uri);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://host:21")]
    [InlineData("not a uri")]
    public void Bad_uris_are_rejected(string? uri) => Assert.Null(RemoteSigner.TryParse(uri));

    [Theory]
    [InlineData("", 7732, false)]
    [InlineData("host name", 7732, false)]
    [InlineData("host;rm", 7732, false)]
    [InlineData("signer.local", 0, false)]
    [InlineData("signer.local", 70000, false)]
    [InlineData("signer.local", 7732, true)]
    [InlineData("10.0.0.5", 443, true)]
    [InlineData("[::1]", 7732, true)]
    public void Validates_host_and_port(string host, int port, bool ok)
        => Assert.Equal(ok, new RemoteSigner(SignerTransport.Tcp, host, port).Validate().Count == 0);

    [Fact]
    public void Reads_key_lists_over_tcp()
    {
        var one = Convert.FromHexString(AliceHash);
        Assert.Equal([Alice, Alice], SignerWire.AddressesFromValue([.. one, .. one]));
        Assert.Equal([Alice], SignerWire.AddressesFromValue([0, 0, 0, 21, .. one]));
        Assert.Equal([Alice], SignerWire.AddressesFromValue(SignerWire.Value([0, .. one])));
        Assert.Throws<SignerWire.MalformedException>(() => SignerWire.AddressesFromValue([0, 0, 0, 22, .. one]));
        Assert.Throws<SignerWire.MalformedException>(() => SignerWire.Value([7]));
        Assert.Equal([0, 1, 6], SignerWire.Frame(SignerWire.KnownKeysRequest));
    }

    [Fact]
    public void Reads_the_refusal_of_a_signer_without_allow_list_known_keys()
    {
        // What an octez-signer started without --allow-list-known-keys answers to known_keys: BSON.
        var listing = Convert.FromHexString("010000005b0000005757000000026b696e64000a00000074656d706f726172790002696400080000006661696c75726500026d736700250000004c697374206b6e6f776e206b6579732072657175657374206e6f7420616c6c6f7765642e0000");
        Assert.Equal("List known keys request not allowed.", Assert.Throws<SignerWire.RefusedException>(() => SignerWire.Value(listing)).Reason);
    }

    [Fact]
    public async Task Lists_keys_from_a_tcp_signer()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var request = new byte[3];
            await stream.ReadExactlyAsync(request);
            Assert.Equal([0, 1, 6], request);
            byte[] reply = [0, .. Convert.FromHexString(AliceHash)];
            await stream.WriteAsync(SignerWire.Frame(reply));
        });

        var keys = await new RemoteSignerClient().ListKeysAsync(new RemoteSigner(SignerTransport.Tcp, "127.0.0.1", port));
        await server;
        Assert.Equal([Alice], keys);
    }

    [Fact]
    public async Task An_unreachable_signer_is_explained()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nothing listens there now
        var ex = await Assert.ThrowsAsync<RemoteSignerException>(
            () => new RemoteSignerClient().ListKeysAsync(new RemoteSigner(SignerTransport.Tcp, "127.0.0.1", port)));
        Assert.StartsWith($"Couldn't reach the signer at tcp://127.0.0.1:{port}", ex.Message);
    }

    [Fact]
    public void Reads_key_lists_over_http()
    {
        Assert.Equal([Alice, Bob], SignerWire.KnownKeysFromJson($$"""{"known_keys":["{{Alice}}","{{Bob}}"]}""", 200));
        Assert.Throws<SignerWire.MalformedException>(() => SignerWire.KnownKeysFromJson("{}", 200));
        var refused = Assert.Throws<SignerWire.RefusedException>(
            () => SignerWire.KnownKeysFromJson("""[{"kind":"temporary","id":"failure","msg":"List known keys request not allowed."}]""", 500));
        Assert.Equal("List known keys request not allowed.", refused.Reason);
    }

    [Fact]
    public void Remote_keys_are_validated()
    {
        Assert.Empty(new RemoteKeys(Tcp, Bob, Carol).Validate());
        Assert.Empty(new RemoteKeys(Tcp, Alice, null).Validate());
        Assert.NotEmpty(new RemoteKeys(Tcp, "", null).Validate());
        Assert.NotEmpty(new RemoteKeys(Tcp, Bob, Bob).Validate());
        Assert.NotEmpty(new RemoteKeys(Tcp, Alice, Bob).Validate());   // companion needs a tz4 consensus key
        Assert.NotEmpty(new RemoteKeys(Tcp, Bob, Alice).Validate());   // companion must be tz4
    }

    [Fact]
    public void Remote_signer_keys_are_allowed_on_mainnet()
        => Assert.True(BakingKeyChoice.IsAllowedOn(BakingKeySetup.RemoteSigner, TezosNetwork.Mainnet));

    /// <summary>A wallet that starts with <paramref name="wallet"/> and gains each imported key.</summary>
    private static FakeHost Wallet(Dictionary<string, string> wallet)
    {
        var host = new FakeHost();
        host.On(a => a is ["octez-client", "show", "address", _], () => new CommandResult(1, "", "no such alias"));
        foreach (var alias in new[] { "consensus", "companion" })
            host.On(a => a is ["octez-client", "show", "address", var x] && x == alias && wallet.ContainsKey(alias),
                () => new CommandResult(0, $"Hash: {wallet[alias]}\nPublic Key: BLpk{alias}\n", ""));
        host.On(a => a is ["octez-client", "import", "secret", "key", _, _], () =>
        {
            var call = host.Calls[^1].Argv;
            wallet[call[4]] = call[5][(call[5].LastIndexOf('/') + 1)..];
            return new CommandResult(0, "", "");
        });
        host.On(a => a is ["octez-client", "create", "bls", "proof", "for", _], stdout: "BLsigProof\n");
        return host;
    }

    [Fact]
    public async Task Imports_the_signers_keys_and_records_the_signer()
    {
        var host = Wallet([]);
        var keys = await new BakingKeyService(host, new OctezClient(host))
            .CreateAsync(new BakingKeyChoice(BakingKeySetup.RemoteSigner, Remote: new RemoteKeys(Tcp, Bob, Carol)));

        Assert.True(host.Ran("octez-client", "import", "secret", "key", "consensus", $"tcp://192.168.1.20:7732/{Bob}"));
        Assert.True(host.Ran("octez-client", "import", "secret", "key", "companion", $"tcp://192.168.1.20:7732/{Carol}"));
        Assert.False(host.Ran("octez-client", "gen", "keys"));
        Assert.Equal([(KeyRole.Consensus, Bob), (KeyRole.Companion, Carol)], keys.Select(k => (k.Role, k.Address)));
        Assert.All(keys, k => Assert.Equal("BLsigProof", k.ProofOfPossession));

        var record = BakingKeysRecord.TryParse(host.Calls.Single(c => c.StdIn is not null).StdIn)!;
        Assert.Equal("RemoteSigner", record.Setup);
        Assert.Equal("tcp://192.168.1.20:7732", record.Signer);
    }

    [Fact]
    public async Task An_alias_for_another_key_is_never_overwritten()
    {
        var host = Wallet(new() { ["consensus"] = Carol });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BakingKeyService(host, new OctezClient(host))
            .CreateAsync(new BakingKeyChoice(BakingKeySetup.RemoteSigner, Remote: new RemoteKeys(Tcp, Bob, null))));
        Assert.False(host.Ran("octez-client", "import"));
    }

    [Fact]
    public async Task A_signer_that_wont_prove_possession_still_works()
    {
        var host = Wallet([]).On(a => a is ["octez-client", "create", "bls", "proof", "for", _], exitCode: 1);
        var keys = await new BakingKeyService(host, new OctezClient(host))
            .CreateAsync(new BakingKeyChoice(BakingKeySetup.RemoteSigner, Remote: new RemoteKeys(Tcp, Bob, null)));
        Assert.Null(Assert.Single(keys).ProofOfPossession);
    }

    [Fact]
    public async Task The_recorded_remote_choice_is_read_back()
    {
        var wallet = new Dictionary<string, string> { ["consensus"] = Bob, ["companion"] = Carol };
        var host = Wallet(wallet).OnFile(OctezPaths.OctezDeskKeysFile,
            new BakingKeysRecord("RemoteSigner", new Dictionary<string, string> { ["Consensus"] = "consensus", ["Companion"] = "companion" },
                "tcp://192.168.1.20:7732").ToJson());
        var service = new BakingKeyService(host, new OctezClient(host));

        var choice = await service.GetRecordedChoiceAsync();
        Assert.Equal(new RemoteKeys(Tcp, Bob, Carol), choice!.Remote);
        Assert.True(await service.HasKeysForAsync(choice));
        Assert.False(await service.HasKeysForAsync(choice with { Remote = new RemoteKeys(Tcp, Bob, null) }));
        Assert.False(await service.HasKeysForAsync(choice with { Remote = new RemoteKeys(Tcp with { Port = 7733 }, Bob, Carol) }));
    }
}
