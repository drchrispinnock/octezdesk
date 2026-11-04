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
    private readonly MainViewModel _main;

    public IReadOnlyList<TezosNetwork> Networks => TezosNetwork.All;
    public IReadOnlyList<HistoryMode> HistoryModes { get; } = Enum.GetValues<HistoryMode>();

    [ObservableProperty] private TezosNetwork _network;
    [ObservableProperty] private HistoryMode _historyMode;
    [ObservableProperty] private string _p2pPort;
    [ObservableProperty] private string _rpcPort;
    [ObservableProperty] private bool _allowAllRpc;
    [ObservableProperty] private bool _importSnapshot;

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
    }

    public string FreeSpace
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(InstallLocation));
                if (string.IsNullOrEmpty(root)) return "";
                var free = new DriveInfo(root).AvailableFreeSpace / (1024.0 * 1024 * 1024);
                return $"{free:N0} GB free on {root}";
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                return "";
            }
        }
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

        // WSL forwards these ports to Windows' localhost, so they must be free there too.
        if (problems.Count == 0)
        {
            var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
            if (listening.Contains(p2p)) problems.Add($"Port {p2p} is already in use on this PC.");
            if (listening.Contains(rpc)) problems.Add($"Port {rpc} is already in use on this PC.");
        }

        if (string.IsNullOrWhiteSpace(InstallLocation)) problems.Add("Choose where to store the node's data.");

        Errors = problems.Count > 0 ? string.Join(Environment.NewLine, problems) : null;
        if (problems.Count == 0) _main.ShowSetupProgress(settings, InstallLocation);
    }
}
