using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctezDesk.Core.Octez;
using OctezDesk.Core.Provisioning;

namespace OctezDesk.Win.ViewModels;

/// <summary>Where the baking keys screen was opened from.</summary>
/// <param name="Settings">Wizard only: the node settings from screen 2, passed on to setup.</param>
public sealed record WizardContext(NodeSettings Settings, string Location);

/// <summary>
/// Wizard screen 3 (Baker): choose whether to bake, which keys to create and the liquidity baking
/// vote. In the wizard, setup generates the keys (octez-client isn't installed yet) and starts the
/// baker; from the dashboard only the keys are generated, here.
/// </summary>
public partial class BakingKeysViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly WizardContext? _wizard;

    public BakingKeysViewModel(MainViewModel main, WizardContext? wizard, BakingKeyChoice? choice, BakerSettings? baker = null)
    {
        _main = main;
        _wizard = wizard;
        _setup = choice?.Setup;
        _masterType = choice?.MasterType ?? KeyType.Tz4;
        _vote = (baker ?? new BakerSettings()).Vote;
        if (wizard is not null) IsMainnet = wizard.Settings.Network == TezosNetwork.Mainnet;
        else _ = LoadNetworkAsync();
    }

    /// <summary>From the dashboard, the network comes from what OctezDesk configured.</summary>
    private async Task LoadNetworkAsync()
    {
        try { IsMainnet = (await Task.Run(() => _main.Services.Node.GetConfiguredSettingsAsync()))?.Network == TezosNetwork.Mainnet.Id; }
        catch (Exception) { IsMainnet = true; } // unknown: offer only what's safe everywhere
    }

    /// <summary>On mainnet only consensus and companion keys are offered (<see cref="BakingKeyChoice.IsAllowedOn"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllowsMasterKey))]
    private bool _isMainnet;

    public bool AllowsMasterKey => !IsMainnet;

    partial void OnIsMainnetChanged(bool value)
    {
        if (value && Setup is { } s && !BakingKeyChoice.IsAllowedOn(s, TezosNetwork.Mainnet)) Setup = null;
    }

    public bool InWizard => _wizard is not null;
    public IReadOnlyList<KeyType> KeyTypes { get; } = Enum.GetValues<KeyType>();

    /// <summary>Null means "don't bake", the default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoBaking), nameof(IsMasterKey), nameof(IsMasterAndCompanion), nameof(IsConsensusAndCompanion),
        nameof(NextLabel), nameof(ShowsVote))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private BakingKeySetup? _setup;

    [ObservableProperty] private KeyType _masterType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVotePass), nameof(IsVoteOn), nameof(IsVoteOff))]
    private LiquidityBakingVote _vote;

    /// <summary>The vote is a baker setting, so it's only asked in the wizard (which starts the baker) and when baking.</summary>
    public bool ShowsVote => InWizard && Setup is not null;

    public bool IsVotePass { get => Vote == LiquidityBakingVote.Pass; set { if (value) Vote = LiquidityBakingVote.Pass; } }
    public bool IsVoteOn { get => Vote == LiquidityBakingVote.On; set { if (value) Vote = LiquidityBakingVote.On; } }
    public bool IsVoteOff { get => Vote == LiquidityBakingVote.Off; set { if (value) Vote = LiquidityBakingVote.Off; } }

    private BakerSettings Baker => new(Vote);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _errors;

    // Radio buttons bind to these.
    public bool IsNoBaking { get => Setup is null; set { if (value) Setup = null; } }
    public bool IsMasterKey { get => Setup == BakingKeySetup.MasterKey; set { if (value) Setup = BakingKeySetup.MasterKey; } }
    public bool IsMasterAndCompanion { get => Setup == BakingKeySetup.MasterAndCompanion; set { if (value) Setup = BakingKeySetup.MasterAndCompanion; } }
    public bool IsConsensusAndCompanion { get => Setup == BakingKeySetup.ConsensusAndCompanion; set { if (value) Setup = BakingKeySetup.ConsensusAndCompanion; } }

    public string NextLabel => InWizard ? "Next" : "Generate";

    private BakingKeyChoice? Choice => Setup is { } s ? new BakingKeyChoice(s, MasterType) : null;

    private bool CanNext() => !IsBusy && (InWizard || Setup is not null);

    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task Next()
    {
        if (_wizard is not null)
        {
            _main.ShowAfterBakingKeys(_wizard.Settings, _wizard.Location, Choice, Baker);
            return;
        }

        IsBusy = true;
        Errors = null;
        try
        {
            var choice = Choice!;
            var keys = await Task.Run(() => _main.Services.Keys.CreateAsync(choice));
            _main.ShowBakingKeysResult(keys);
        }
        catch (Exception ex)
        {
            Errors = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void Back()
    {
        if (_wizard is not null)
        {
            _main.RememberBaker(Baker);
            _main.ShowNodeSetup(_wizard.Settings, _wizard.Location, Choice);
        }
        else _main.ShowDashboard();
    }

    private bool NotBusy() => !IsBusy;
}

/// <summary>One labelled value on a key card, e.g. "Public key".</summary>
public sealed record KeyField(string Label, string Value);

/// <summary>A key as shown on the results screen.</summary>
public sealed record KeyCard(string Title, IReadOnlyList<KeyField> Fields);

/// <summary>Shows the baking keys: public keys, proofs of possession and what to do with them.</summary>
public partial class BakingKeysResultViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public BakingKeysResultViewModel(MainViewModel main, IReadOnlyList<BakingKey> keys)
    {
        _main = main;
        _hasCompanion = keys.Any(k => k.Role == KeyRole.Companion);
        HasMasterKey = keys.Any(k => k.Role == KeyRole.Master);
        // A master key OctezDesk holds is registered from here, so the proofs of possession aren't
        // needed; they're only for setting a consensus key from another wallet.
        Keys = keys.Select(k => ToCard(k, showProof: !HasMasterKey)).ToList();
        if (HasMasterKey)
        {
            _nextStepsIntro = "Checking the master key on chain…";
            _ = RefreshAsync();
        }
        else (_nextStepsIntro, NextSteps) = Describe(keys);
    }

    private readonly bool _hasCompanion;

    public IReadOnlyList<KeyCard> Keys { get; }

    /// <summary>True for "Master key" and "Master and companion": the next steps come from the chain, and OctezDesk can register.</summary>
    public bool HasMasterKey { get; }

    [ObservableProperty] private string _nextStepsIntro;

    /// <summary>Commands to run elsewhere; only for a consensus key whose master key is in another wallet.</summary>
    public IReadOnlyList<KeyField> NextSteps { get; } = [];

    /// <summary>The master key's on-chain state, one line each.</summary>
    [ObservableProperty] private IReadOnlyList<string> _status = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private bool _canRegister;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand), nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _errors;

    private static string Tez(long mutez) => (mutez / 1_000_000m).ToString("#,0.######") + " ꜩ";

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var s = await Task.Run(() => _main.Services.Account.TryGetStatusAsync());
            ShowStatus(s);
        }
        finally { IsBusy = false; }
    }

    private void ShowStatus(MasterAccountStatus? s)
    {
        CanRegister = false;
        if (s is null)
        {
            NextStepsIntro = "Couldn't read the master key's state from the node. It may still be starting; try Refresh in a moment.";
            Status = [];
            return;
        }

        var lines = new List<string> { $"Spendable: {Tez(s.BalanceMutez)}  ·  staked: {Tez(s.StakedMutez)}" };
        if (s.Delegate is { } d)
        {
            NextStepsIntro = "The master key is registered as a baker.";
            if (_hasCompanion)
                lines.Add(d.CompanionActive is not null ? "Companion key: active."
                    : d.CompanionPending is not null ? $"Companion key: set, active from cycle {d.CompanionPendingCycle}."
                    : "Companion key: not set.");
            lines.Add(s.StakedMutez >= FundingSettings.MinimumStake * 1_000_000L
                ? "Baking rights start a few cycles after staking. Watch the baker's log for its first blocks and attestations."
                : $"Stake at least {FundingSettings.MinimumStake:N0} ꜩ to get baking rights.");
        }
        else if (s.BalanceMutez == 0)
        {
            NextStepsIntro = $"Send tez to the master key's address ({s.Address}), then register it as a baker here.";
        }
        else
        {
            NextStepsIntro = "The master key has funds. Register it as a baker" + (_hasCompanion ? ", with its companion key:" : ":");
            CanRegister = true;
        }
        Status = lines;
    }

    private bool CanRunRegister() => CanRegister && !IsBusy;

    /// <summary>Registers the master key as a delegate (with the companion key), from the OctezDesk wallet.</summary>
    [RelayCommand(CanExecute = nameof(CanRunRegister))]
    private async Task RegisterAsync()
    {
        IsBusy = true;
        Errors = null;
        try
        {
            NextStepsIntro = "Registering as a baker. This waits for the node to be bootstrapped and the operation to be included…";
            await Task.Run(() => _main.Services.Account.RegisterAsync(_hasCompanion));
        }
        catch (Exception ex) { Errors = ex.Message; }
        finally { IsBusy = false; }
        await RefreshAsync();
    }

    private bool NotBusy() => !IsBusy;

    private static KeyCard ToCard(BakingKey k, bool showProof)
    {
        var role = k.Role switch
        {
            KeyRole.Master => "Master key",
            KeyRole.Consensus => "Consensus key",
            KeyRole.Companion => "Companion key",
            _ => k.Role.ToString(),
        };
        var fields = new List<KeyField> { new("Address", k.Address), new("Public key", k.PublicKey) };
        if (showProof && k.ProofOfPossession is { } pop) fields.Add(new("Proof of possession", pop));
        return new KeyCard($"{role}  ·  wallet alias \"{k.Alias}\"", fields);
    }

    private static (string, IReadOnlyList<KeyField>) Describe(IReadOnlyList<BakingKey> keys)
    {
        var byRole = keys.ToDictionary(k => k.Role);
        var companion = byRole.GetValueOrDefault(KeyRole.Companion);

        if (byRole.TryGetValue(KeyRole.Consensus, out var consensus))
        {
            var consensusArgs = $"--consensus-key {consensus.PublicKey} --consensus-key-pop {consensus.ProofOfPossession}";
            var companionArgs = companion is null ? "" : $" --companion-key {companion.PublicKey} --companion-key-pop {companion.ProofOfPossession}";
            var steps = new List<KeyField>
            {
                // One operation: register, set both keys, stake the minimum.
                new("New baker",
                    $"octez-client register key <baker> as delegate {consensusArgs}{companionArgs} --initial-stake {FundingSettings.MinimumStake}"),
                new("Already a baker: consensus key",
                    $"octez-client set consensus key for <baker> to {consensus.PublicKey} --consensus-key-pop {consensus.ProofOfPossession}"),
            };
            if (companion is not null)
                steps.Add(new("Already a baker: companion key",
                    $"octez-client set companion key for <baker> to {companion.PublicKey} --companion-key-pop {companion.ProofOfPossession}"));
            steps.Add(new("Stake, if needed", $"octez-client stake {FundingSettings.MinimumStake} for <baker>"));
            return ($"Your baker's master key stays in your own wallet (octez-client, Signet or a Ledger). Its account needs at least " +
                    $"{FundingSettings.MinimumStake:N0} ꜩ to stake, plus a little for fees. From that wallet, replacing <baker> with your " +
                    "baker's address, either register a new baker with the first command, or set the keys on an existing one. " +
                    "Wallets other than octez-client ask for the public keys and proofs of possession shown above. " +
                    "The keys take effect a few cycles later; until then the baker here has nothing to sign.", steps);
        }

        return ("", []); // a master key's next steps come from the chain (ShowStatus)
    }

    [RelayCommand]
    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { } // clipboard busy; the user can try again
    }

    [RelayCommand]
    private void Continue() => _main.ShowDashboard();
}
