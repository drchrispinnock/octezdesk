using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctezDesk.Core.Octez;

namespace OctezDesk.Win.ViewModels;

/// <summary>
/// Wizard screen 4: the DAL node. On by default when baking. It attests for the master key made on
/// the baking keys screen; otherwise it asks for the baker's master key address.
/// </summary>
public partial class DalSetupViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly WizardContext _wizard;
    private readonly BakingKeyChoice? _keys;
    private readonly BakerSettings _baker;

    public DalSetupViewModel(MainViewModel main, WizardContext wizard, BakingKeyChoice? keys, BakerSettings baker, DalSettings? previous)
    {
        _main = main;
        _wizard = wizard;
        _keys = keys;
        _baker = baker;
        var s = previous ?? new DalSettings { Enabled = IsBaking };
        _enabled = s.Enabled;
        _attesterAddress = s.AttesterAddress ?? "";
        _rpcPort = s.RpcPort.ToString();
        _p2pPort = s.P2PPort.ToString();
    }

    public bool IsBaking => _keys is not null;

    /// <summary>True when the baking keys screen creates a master key, which the DAL node then attests for.</summary>
    public bool HasMasterKey => _keys?.Setup is BakingKeySetup.MasterKey or BakingKeySetup.MasterAndCompanion;

    public bool AsksForAttester => !HasMasterKey;

    public string AttesterHint => HasMasterKey
        ? $"The DAL node will attest for the master key \"{BakingKeyService.MasterAlias}\" that setup creates."
        : IsBaking
            ? "The address (tz1, tz2, tz3 or tz4) of your baker's master key, which stays in your other wallet. The DAL node attests for it."
            : "Optional. The address of a baker to attest for. Leave empty to run the DAL node without an attester profile.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Warning))]
    private bool _enabled;

    [ObservableProperty] private string _attesterAddress;
    [ObservableProperty] private string _rpcPort;
    [ObservableProperty] private string _p2pPort;
    [ObservableProperty] private string? _errors;

    public string? Warning => IsBaking && !Enabled
        ? "Without a DAL node your baker can't take part in DAL attestations, and it misses the DAL rewards."
        : null;

    /// <summary>Builds the settings from the fields; null (with <see cref="Errors"/> set) if they're invalid.</summary>
    private DalSettings? TryBuild(bool report)
    {
        var problems = new List<string>();
        if (!int.TryParse(RpcPort, out var rpc)) problems.Add("DAL RPC port must be a number.");
        if (!int.TryParse(P2pPort, out var p2p)) problems.Add("DAL P2P port must be a number.");

        var settings = new DalSettings
        {
            Enabled = Enabled,
            AttestForMasterKey = HasMasterKey,
            AttesterAddress = HasMasterKey || string.IsNullOrWhiteSpace(AttesterAddress) ? null : AttesterAddress.Trim(),
            RpcPort = rpc,
            P2PPort = p2p,
        };
        if (Enabled && problems.Count == 0) problems.AddRange(settings.Validate(_wizard.Settings, IsBaking));

        if (report) Errors = problems.Count > 0 ? string.Join(Environment.NewLine, problems) : null;
        return problems.Count == 0 || !Enabled ? settings : null;
    }

    [RelayCommand]
    private void Back()
    {
        _main.RememberDal(TryBuild(report: false), IsBaking);
        _main.ShowBeforeDal(_wizard.Settings, _wizard.Location, _keys, _baker);
    }

    [RelayCommand]
    private void SetUp()
    {
        if (TryBuild(report: true) is not { } settings) return;
        _main.ShowSetupProgress(_wizard.Settings, _wizard.Location, _keys, settings, _baker);
    }
}
