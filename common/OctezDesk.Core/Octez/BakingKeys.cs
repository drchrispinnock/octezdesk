using System.Text.Json;
using System.Text.RegularExpressions;
using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Octez;

/// <summary>Tez amounts as octez-client takes them (<c>DDDDDDD.DDDDDD</c>), and as people type them.</summary>
public static class TezAmount
{
    public const long MutezPerTez = 1_000_000;

    /// <summary>e.g. 6000000000 → "6000", 1500000 → "1.5".</summary>
    public static string Format(long mutez)
        => (mutez / (decimal)MutezPerTez).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Parses a positive amount in tez with at most 6 decimals ("1,000.5" is accepted); null if it isn't one.</summary>
    public static long? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().Replace(",", "").Replace("ꜩ", "").Trim();
        if (!decimal.TryParse(s, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var tez))
            return null;
        var mutez = tez * MutezPerTez;
        return tez > 0 && mutez == decimal.Truncate(mutez) && mutez <= long.MaxValue ? (long)mutez : null;
    }

    /// <summary>Why an unstake of <paramref name="text"/> can't go ahead, or null if it can.</summary>
    public static string? ValidateUnstake(string? text, long stakedMutez)
    {
        if (TryParse(text) is not { } mutez) return "Enter an amount in tez, for example 1000 or 0.5.";
        if (mutez > stakedMutez) return $"Only {Format(stakedMutez)} ꜩ is staked.";
        return null;
    }
}

/// <summary>Tezos implicit account types, by address prefix.</summary>
public enum KeyType
{
    Tz1,
    Tz2,
    Tz3,
    Tz4,
}

public static class KeyTypeExtensions
{
    /// <summary>The <c>octez-client gen keys --sig</c> value.</summary>
    public static string ToSig(this KeyType t) => t switch
    {
        KeyType.Tz1 => "ed25519",
        KeyType.Tz2 => "secp256k1",
        KeyType.Tz3 => "p256",
        KeyType.Tz4 => "bls",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public static string Prefix(this KeyType t) => t.ToString().ToLowerInvariant();

    public static string Describe(this KeyType t) => t switch
    {
        KeyType.Tz1 => "tz1 (Ed25519)",
        KeyType.Tz2 => "tz2 (secp256k1)",
        KeyType.Tz3 => "tz3 (P-256)",
        KeyType.Tz4 => "tz4 (BLS)",
        _ => t.ToString(),
    };

    public static KeyType? FromAddress(string address)
        => Enum.GetValues<KeyType>().Cast<KeyType?>().FirstOrDefault(t => address.StartsWith(t!.Value.Prefix(), StringComparison.Ordinal));
}

/// <summary>What the user chose on the baking keys screen (wizard screen 3).</summary>
public enum BakingKeySetup
{
    /// <summary>A master (manager) key of the chosen type, which also signs blocks and attestations.</summary>
    MasterKey,
    /// <summary>A tz4 master key plus a tz4 companion key for DAL attestations.</summary>
    MasterAndCompanion,
    /// <summary>
    /// tz4 consensus and companion keys only, for a baker whose master key is kept elsewhere (a
    /// wallet or Ledger). The user registers them from there, using the public keys and proofs.
    /// </summary>
    ConsensusAndCompanion,
}

public enum KeyRole
{
    Master,
    Consensus,
    Companion,
}

/// <param name="ProofOfPossession">BLS proof of possession (tz4 keys only), needed to register the key on chain.</param>
/// <param name="AlreadyExisted">True if the alias was already in the wallet and was reused, not generated.</param>
public sealed record BakingKey(
    KeyRole Role, string Alias, string Address, string PublicKey, string? ProofOfPossession, bool AlreadyExisted);

/// <summary>
/// Runs <c>octez-client</c> as the tezos user, using its default wallet (<c>~tezos/.tezos-client</c>),
/// which is the one the packaged baker reads.
/// </summary>
public sealed class OctezClient(IOctezHost host)
{
    private static readonly Regex HashLine = new(@"^Hash:\s*(\S+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PublicKeyLine = new(@"^Public Key:\s*(\S+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex BlsProof = new(@"^(BLsig\S+)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private Task<CommandResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct, Action<string>? log = null)
        => host.RunAsync(["octez-client", .. args], HostUser.Tezos, onLine: log, ct: ct);

    /// <summary>Waits until the local node is bootstrapped, so that operations are injected on the current chain.</summary>
    public async Task WaitForBootstrappedAsync(Action<string>? log = null, CancellationToken ct = default)
        => (await RunAsync(["bootstrapped"], ct, log)).EnsureSuccess("Waiting for the node to be bootstrapped");

    /// <summary>
    /// Registers <paramref name="alias"/> as a delegate (revealing it first if needed), optionally
    /// with a companion key. octez-client waits until the operation is included in a block.
    /// </summary>
    public async Task RegisterDelegateAsync(string alias, string? companionAlias, Action<string>? log = null, CancellationToken ct = default)
    {
        string[] args = companionAlias is null
            ? ["register", "key", alias, "as", "delegate"]
            : ["register", "key", alias, "as", "delegate", "--companion-key", companionAlias];
        (await RunAsync(args, ct, log)).EnsureSuccess($"Registering '{alias}' as a delegate");
    }

    /// <summary>
    /// Unstakes <paramref name="mutez"/> from <paramref name="alias"/>'s stake, or everything if null.
    /// The tez stay frozen for a few cycles, then need <see cref="FinalizeUnstakeAsync"/>.
    /// </summary>
    public async Task UnstakeAsync(string alias, long? mutez, Action<string>? log = null, CancellationToken ct = default)
        => (await RunAsync(["unstake", mutez is { } m ? TezAmount.Format(m) : "everything", "for", alias], ct, log))
            .EnsureSuccess($"Unstaking for '{alias}'");

    /// <summary>Moves <paramref name="alias"/>'s finalizable unstaked tez to its spendable balance.</summary>
    public async Task FinalizeUnstakeAsync(string alias, Action<string>? log = null, CancellationToken ct = default)
        => (await RunAsync(["finalize", "unstake", "for", alias], ct, log)).EnsureSuccess($"Finalizing the unstake for '{alias}'");

    /// <summary>Stakes <paramref name="tez"/> ꜩ from <paramref name="alias"/>'s own balance.</summary>
    public async Task StakeAsync(string alias, long tez, Action<string>? log = null, CancellationToken ct = default)
        => (await RunAsync(["stake", tez.ToString(System.Globalization.CultureInfo.InvariantCulture), "for", alias], ct, log))
            .EnsureSuccess($"Staking {tez} ꜩ for '{alias}'");

    /// <summary>The address and public key stored under <paramref name="alias"/>, or null if there is none.</summary>
    public async Task<(string Address, string PublicKey)?> ShowAddressAsync(string alias, CancellationToken ct = default)
    {
        var r = await RunAsync(["show", "address", alias], ct);
        if (!r.Success) return null;
        var hash = HashLine.Match(r.StdOut);
        var pk = PublicKeyLine.Match(r.StdOut);
        return hash.Success && pk.Success ? (hash.Groups[1].Value, pk.Groups[1].Value) : null;
    }

    /// <summary>Generates an unencrypted key, so that the baker can use it unattended. Fails if the alias exists.</summary>
    public async Task GenerateKeysAsync(string alias, KeyType type, CancellationToken ct = default)
        => (await RunAsync(["gen", "keys", alias, "--sig", type.ToSig()], ct)).EnsureSuccess($"Generating key '{alias}'");

    public async Task<string> CreateBlsProofAsync(string alias, CancellationToken ct = default)
    {
        var r = (await RunAsync(["create", "bls", "proof", "for", alias], ct)).EnsureSuccess($"Creating a proof of possession for '{alias}'");
        var m = BlsProof.Match(r.StdOut);
        return m.Success ? m.Groups[1].Value : throw new InvalidOperationException($"Unexpected output from create bls proof: {r.StdOut.Trim()}");
    }
}

/// <summary>Creates the keys for a <see cref="BakingKeySetup"/>, and records which ones OctezDesk set up.</summary>
public sealed class BakingKeyService(IOctezHost host, OctezClient client)
{
    public const string MasterAlias = "baker";
    public const string ConsensusAlias = "consensus";
    public const string CompanionAlias = "companion";

    /// <summary>The keys a setup needs: role, wallet alias and type.</summary>
    public static IReadOnlyList<(KeyRole Role, string Alias, KeyType Type)> Plan(BakingKeySetup setup, KeyType masterType)
        => setup switch
        {
            BakingKeySetup.MasterKey => [(KeyRole.Master, MasterAlias, masterType)],
            BakingKeySetup.MasterAndCompanion =>
                [(KeyRole.Master, MasterAlias, KeyType.Tz4), (KeyRole.Companion, CompanionAlias, KeyType.Tz4)],
            BakingKeySetup.ConsensusAndCompanion =>
                [(KeyRole.Consensus, ConsensusAlias, KeyType.Tz4), (KeyRole.Companion, CompanionAlias, KeyType.Tz4)],
            _ => throw new ArgumentOutOfRangeException(nameof(setup)),
        };

    /// <summary>
    /// Generates each key in the plan. A key whose alias already exists is reused, never
    /// overwritten (it may hold funds), as long as it is of the right type.
    /// </summary>
    public async Task<IReadOnlyList<BakingKey>> CreateAsync(
        BakingKeyChoice choice, Action<string>? log = null, CancellationToken ct = default)
    {
        var setup = choice.Setup;
        var plan = Plan(setup, choice.MasterType);

        // Check every existing alias first, so a type mismatch fails before anything is generated.
        var existing = new Dictionary<string, (string Address, string PublicKey)>();
        foreach (var (_, alias, type) in plan)
        {
            if (await client.ShowAddressAsync(alias, ct) is not { } key) continue;
            if (KeyTypeExtensions.FromAddress(key.Address) != type)
                throw new InvalidOperationException(
                    $"The wallet already has a key called '{alias}' ({key.Address}), but this setup needs a {type.Prefix()} key. " +
                    "OctezDesk never overwrites keys; rename or remove the existing one with octez-client first.");
            existing[alias] = key;
        }

        var keys = new List<BakingKey>();
        foreach (var (role, alias, type) in plan)
        {
            var reused = existing.TryGetValue(alias, out var found);
            if (reused)
            {
                log?.Invoke($"Using the existing key '{alias}' ({found.Address}).");
            }
            else
            {
                log?.Invoke($"Generating {type.Describe()} key '{alias}'…");
                await client.GenerateKeysAsync(alias, type, ct);
                found = await client.ShowAddressAsync(alias, ct)
                        ?? throw new InvalidOperationException($"Key '{alias}' was generated but cannot be read back.");
            }

            var pop = type == KeyType.Tz4 ? await client.CreateBlsProofAsync(alias, ct) : null;
            keys.Add(new BakingKey(role, alias, found.Address, found.PublicKey, pop, reused));
        }

        await host.WriteFileAsync(OctezPaths.OctezDeskKeysFile, BakingKeysRecord.From(setup, keys).ToJson(), ct);
        return keys;
    }

    public async Task<BakingKeysRecord?> GetRecordAsync(CancellationToken ct = default)
        => BakingKeysRecord.TryParse(await host.TryReadFileAsync(OctezPaths.OctezDeskKeysFile, ct));

    /// <summary>The choice OctezDesk recorded when it set up the keys, e.g. to prefill the wizard; null if none.</summary>
    public async Task<BakingKeyChoice?> GetRecordedChoiceAsync(CancellationToken ct = default)
    {
        if (await GetRecordAsync(ct) is not { } record || !Enum.TryParse<BakingKeySetup>(record.Setup, out var setup)) return null;
        var masterType = KeyType.Tz4;
        if (setup == BakingKeySetup.MasterKey && await client.ShowAddressAsync(MasterAlias, ct) is { } master)
            masterType = KeyTypeExtensions.FromAddress(master.Address) ?? KeyType.Tz4;
        return new BakingKeyChoice(setup, masterType);
    }

    /// <summary>True if the keys for <paramref name="choice"/> are recorded and all present in the wallet with the right types.</summary>
    public async Task<bool> HasKeysForAsync(BakingKeyChoice choice, CancellationToken ct = default)
    {
        if ((await GetRecordAsync(ct))?.Setup != choice.Setup.ToString()) return false;
        foreach (var (_, alias, type) in Plan(choice.Setup, choice.MasterType))
            if (await client.ShowAddressAsync(alias, ct) is not { } key || KeyTypeExtensions.FromAddress(key.Address) != type)
                return false;
        return true;
    }

    /// <summary>
    /// The keys OctezDesk set up, read back from the wallet with fresh proofs of possession, or an
    /// empty list if none are recorded. Recorded keys missing from the wallet are left out.
    /// </summary>
    public async Task<IReadOnlyList<BakingKey>> GetKeysAsync(CancellationToken ct = default)
    {
        if (await GetRecordAsync(ct) is not { } record) return [];
        var keys = new List<BakingKey>();
        foreach (var (roleName, alias) in record.Aliases)
        {
            if (!Enum.TryParse<KeyRole>(roleName, out var role)) continue;
            if (await client.ShowAddressAsync(alias, ct) is not { } key) continue;
            var pop = KeyTypeExtensions.FromAddress(key.Address) == KeyType.Tz4 ? await client.CreateBlsProofAsync(alias, ct) : null;
            keys.Add(new BakingKey(role, alias, key.Address, key.PublicKey, pop, AlreadyExisted: true));
        }
        return keys.OrderBy(k => k.Role).ToList();
    }
}

/// <summary>The baking keys screen's answer: which setup, and the master key type for <see cref="BakingKeySetup.MasterKey"/>.</summary>
public sealed record BakingKeyChoice(BakingKeySetup Setup, KeyType MasterType = KeyType.Tz4)
{
    /// <summary>
    /// On mainnet only consensus and companion keys are created here: the master key holds real
    /// funds, so it belongs in the user's own wallet or a Ledger, not unencrypted in the distro.
    /// </summary>
    public static bool IsAllowedOn(BakingKeySetup setup, TezosNetwork network)
        => network != TezosNetwork.Mainnet || setup == BakingKeySetup.ConsensusAndCompanion;
}

/// <summary>
/// Which keys OctezDesk set up for baking, kept in the distro so later screens (DAL node, baker)
/// can default to them. Holds aliases and public data only, never secrets.
/// </summary>
public sealed record BakingKeysRecord(string Setup, IReadOnlyDictionary<string, string> Aliases)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static BakingKeysRecord From(BakingKeySetup setup, IEnumerable<BakingKey> keys)
        => new(setup.ToString(), keys.ToDictionary(k => k.Role.ToString(), k => k.Alias));

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static BakingKeysRecord? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<BakingKeysRecord>(json); }
        catch (JsonException) { return null; }
    }
}
