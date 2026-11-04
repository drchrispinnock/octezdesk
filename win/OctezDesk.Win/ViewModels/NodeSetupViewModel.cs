using System.IO;
using System.Net.NetworkInformation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OctezDesk.Core.Octez;

namespace OctezDesk.Win.ViewModels;

/// <summary>Wizard screen 2: node settings.</summary>
public partial class NodeSetupViewModel : ObservableObject
{
    /// <summary>
    /// Rough disk needed relative to the snapshot file: the download plus the imported store, with
    /// headroom for growth. A guide for the warning only.
    /// </summary>
    private const double DiskPerSnapshotByte = 3.0;

    private readonly MainViewModel _main;
    private CancellationTokenSource? _sizeCts;
    private long? _snapshotBytes;

    public IReadOnlyList<TezosNetwork> Networks => TezosNetwork.All;
    public IReadOnlyList<HistoryMode> HistoryModes { get; } = Enum.GetValues<HistoryMode>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetworkHint))]
    private TezosNetwork _network;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryModeHint), nameof(CanImportSnapshot))]
    private HistoryMode _historyMode;

    [ObservableProperty] private string _p2pPort;
    [ObservableProperty] private string _rpcPort;
    [ObservableProperty] private bool _allowAllRpc;
    [ObservableProperty] private bool _importSnapshot;
    [ObservableProperty] private string _snapshotHint = "";
    [ObservableProperty] private string? _spaceWarning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FreeSpace))]
    private string _installLocation;

    [ObservableProperty] private string? _errors;

    public NodeSetupViewModel(MainViewModel main, NodeSettings settings, string? location)
    {
        _main = main;
        _network = settings.Network;
        _historyMode = settings.HistoryMode;
        _p2pPort = settings.P2PPort.ToString();
        _rpcPort = settings.RpcPort.ToString();
        _allowAllRpc = settings.AllowAllRpc;
        _importSnapshot = settings.ImportSnapshot;
        _installLocation = location ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OctezDesk", "wsl");
        _ = RefreshSnapshotAsync();
    }

    public string NetworkHint => Network == TezosNetwork.Mainnet
        ? "The live Tezos network."
        : "A test network with free test tez. Good for trying things out.";

    public string HistoryModeHint => HistoryMode switch
    {
        HistoryMode.Rolling => "Keeps only recent blocks. Smallest and fastest to set up; recommended for bakers.",
        HistoryMode.Full => "Keeps every block but only recent ledger state. Larger; useful for some services.",
        HistoryMode.Archive => "Keeps everything. Needs a lot of disk and syncs from the very first block (days).",
        _ => "",
    };

    /// <summary>Archive nodes cannot start from a snapshot.</summary>
    public bool CanImportSnapshot => HistoryMode != HistoryMode.Archive;

    partial void OnNetworkChanged(TezosNetwork value) => _ = RefreshSnapshotAsync();

    partial void OnHistoryModeChanged(HistoryMode value)
    {
        if (value == HistoryMode.Archive) ImportSnapshot = false;
        _ = RefreshSnapshotAsync();
    }

    partial void OnImportSnapshotChanged(bool value) => _ = RefreshSnapshotAsync();

    partial void OnInstallLocationChanged(string value) => UpdateSpaceWarning();

    private async Task RefreshSnapshotAsync()
    {
        _sizeCts?.Cancel();
        _snapshotBytes = null;
        UpdateSpaceWarning();
        if (!ImportSnapshot)
        {
            SnapshotHint = HistoryMode == HistoryMode.Archive
                ? "Archive nodes sync from the first block instead."
                : "Without a snapshot the node syncs from the first block, which takes days.";
            return;
        }

        var cts = _sizeCts = new CancellationTokenSource();
        SnapshotHint = "Checking snapshot size…";
        var size = await SnapshotCatalog.TryGetSizeAsync(Network.SnapshotUrl(HistoryMode), cts.Token);
        if (cts.IsCancellationRequested) return;

        _snapshotBytes = size;
        SnapshotHint = size is { } bytes
            ? $"Downloads {SnapshotCatalog.FormatSize(bytes)} from snapshots.tzinit.org."
            : "Downloads a snapshot from snapshots.tzinit.org (size unavailable right now).";
        UpdateSpaceWarning();
    }

    private long? FreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(InstallLocation));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public string FreeSpace => FreeBytes() is { } free
        ? $"{SnapshotCatalog.FormatSize(free)} free on {Path.GetPathRoot(Path.GetFullPath(InstallLocation))}"
        : "";

    private void UpdateSpaceWarning()
    {
        SpaceWarning = _snapshotBytes is { } snap && FreeBytes() is { } free && free < snap * DiskPerSnapshotByte
            ? $"Low disk space: allow at least {SnapshotCatalog.FormatSize((long)(snap * DiskPerSnapshotByte))} for this node."
            : null;
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Where should OctezDesk store the node's Linux disk?" };
        if (dialog.ShowDialog() == true) InstallLocation = Path.Combine(dialog.FolderName, "OctezDesk");
    }

    [RelayCommand]
    private void Back() => _main.ShowWelcome();

    [RelayCommand]
    private void SetUp()
    {
        var problems = new List<string>();
        if (!int.TryParse(P2pPort, out var p2p)) problems.Add("Gossip port must be a number.");
        if (!int.TryParse(RpcPort, out var rpc)) problems.Add("RPC port must be a number.");

        var settings = new NodeSettings
        {
            Network = Network,
            HistoryMode = HistoryMode,
            P2PPort = p2p,
            RpcPort = rpc,
            AllowAllRpc = AllowAllRpc,
            ImportSnapshot = ImportSnapshot,
        };
        if (problems.Count == 0) problems.AddRange(settings.Validate());

        // With WSL mirrored networking the distro shares Windows' ports, so these would clash.
        if (problems.Count == 0)
        {
            var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
            if (listening.Contains(p2p)) problems.Add($"Port {p2p} is already in use on this PC.");
            if (listening.Contains(rpc)) problems.Add($"Port {rpc} is already in use on this PC.");
        }

        if (string.IsNullOrWhiteSpace(InstallLocation)) problems.Add("Choose where to store the node's data.");

        Errors = problems.Count > 0 ? string.Join(Environment.NewLine, problems) : null;
        if (problems.Count == 0) _main.ShowBakingKeys(settings, InstallLocation);
    }
}
