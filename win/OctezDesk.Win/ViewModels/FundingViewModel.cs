using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctezDesk.Core.Octez;

namespace OctezDesk.Win.ViewModels;

/// <summary>
/// Wizard screen between Baker and DAL node, shown on test networks when setup creates a master
/// key: get test tez from the faucet, and stake. Setup does both once the key exists and the node
/// is up. Mainnet funding comes later.
/// </summary>
public partial class FundingViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly WizardContext _wizard;
    private readonly BakingKeyChoice _keys;
    private readonly BakerSettings _baker;
    private int? _faucetMax;

    public FundingViewModel(MainViewModel main, WizardContext wizard, BakingKeyChoice keys, BakerSettings baker, FundingSettings? previous)
    {
        _main = main;
        _wizard = wizard;
        _keys = keys;
        _baker = baker;
        var s = previous ?? new FundingSettings();
        _fund = s.Fund;
        _stake = s.Stake;
        _amount = s.Amount.ToString();
        _ = LoadFaucetInfoAsync();
    }

    public string NetworkName => _wizard.Settings.Network.DisplayName;

    public string FundLabel => $"Get test tez from the {NetworkName} faucet";

    /// <summary>Whether the faucet sets proof-of-work challenges; assumed until its /info says otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FundHint))]
    private bool _faucetHasChallenges = true;

    public string FundHint =>
        $"Setup creates the master key \"{BakingKeyService.MasterAlias}\" (or uses the one already there), then asks " +
        $"{_wizard.Settings.Network.FaucetUrl} to send it this amount. " +
        (FaucetHasChallenges ? "The faucet makes the computer solve proof-of-work puzzles first, which can take a few minutes. " : "") +
        "Test tez have no value.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StakeLabel))]
    private bool _fund;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StakeLabel))]
    private string _amount;

    [ObservableProperty] private bool _stake;
    [ObservableProperty] private string? _faucetLimit;
    [ObservableProperty] private string? _errors;

    public string StakeLabel => $"Register as a baker and stake {StakeAmountText} ꜩ";

    private string StakeAmountText => int.TryParse(Amount, out var a)
        ? new FundingSettings { Amount = a }.StakeAmount.ToString("N0")
        : FundingSettings.MinimumStake.ToString("N0");

    public string StakeHint =>
        $"Registers the master key as a delegate{(_keys.Setup == BakingKeySetup.MasterAndCompanion ? ", with its companion key," : "")} " +
        $"and stakes {FundingSettings.MinimumStake:N0} ꜩ or 10% of the amount, whichever is higher. Baking rights start a few cycles later.";

    private async Task LoadFaucetInfoAsync()
    {
        try
        {
            var info = await Faucet.For(_wizard.Settings.Network).GetInfoAsync();
            _faucetMax = info.MaxTez;
            FaucetHasChallenges = info.ChallengesEnabled;
            FaucetLimit = $"The {NetworkName} faucet gives up to {info.MaxTez:N0} ꜩ per request.";
        }
        catch (Exception)
        {
            FaucetLimit = $"Couldn't reach the {NetworkName} faucet just now. Setup will try again.";
        }
    }

    private FundingSettings? TryBuild(bool report)
    {
        var problems = new List<string>();
        if (!int.TryParse(Amount, out var amount) && Fund) problems.Add("The amount must be a whole number of tez.");
        var settings = new FundingSettings { Fund = Fund, Amount = amount > 0 ? amount : FundingSettings.MinimumAmount, Stake = Fund && Stake };
        if (problems.Count == 0) problems.AddRange(settings.Validate(_faucetMax));
        if (report) Errors = problems.Count > 0 ? string.Join(Environment.NewLine, problems) : null;
        return problems.Count == 0 ? settings : null;
    }

    [RelayCommand]
    private void Back()
    {
        if (TryBuild(report: false) is { } s) _main.RememberFunding(s);
        _main.ShowBakingKeys(_wizard.Settings, _wizard.Location);
    }

    [RelayCommand]
    private void Next()
    {
        if (TryBuild(report: true) is not { } settings) return;
        _main.RememberFunding(settings);
        _main.ShowDalSetup(_wizard.Settings, _wizard.Location, _keys, _baker);
    }
}
