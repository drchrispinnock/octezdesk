using System.Windows;
using OctezDesk.Core;
using OctezDesk.Core.Health;
using OctezDesk.Core.Octez;
using OctezDesk.Core.Provisioning;
using OctezDesk.Core.Wsl;
using OctezDesk.Win.ViewModels;

namespace OctezDesk.Win;

/// <summary>Shared services, created once in <see cref="App.OnStartup"/>.</summary>
public sealed class AppServices
{
    public WslManager Wsl { get; } = new();
    public WslHost Host { get; }
    public NodeService Node { get; }
    public NodeMonitor Monitor { get; }
    public InstallationProbe Probe { get; }
    public BakingKeyService Keys { get; }
    /// <summary>The master key's on-chain state (balance, stake, delegate registration).</summary>
    public MasterAccount Account { get; }
    /// <summary>Updates OctezDesk itself (Velopack, from GitHub Releases).</summary>
    public AppUpdater Updater { get; } = new();
    public DalNodeService Dal { get; }
    public DalMonitor DalMonitor { get; }
    public BakerService Baker { get; }
    public BakerMonitor BakerMonitor { get; }

    public AppServices()
    {
        Host = new WslHost(Wsl.DistroName);
        Node = new NodeService(Host);
        Monitor = new NodeMonitor(Host, Node);
        Probe = new InstallationProbe(Wsl, Node);
        var client = new OctezClient(Host);
        Keys = new BakingKeyService(Host, client);
        Account = new MasterAccount(Host, Node, client);
        Dal = new DalNodeService(Host);
        DalMonitor = new DalMonitor(Dal);
        Baker = new BakerService(Host);
        BakerMonitor = new BakerMonitor(Baker);
    }
}

/// <summary>
/// The GUI only configures and monitors. The node runs under systemd in WSL and is kept alive by
/// <see cref="KeepAliveService"/>, so exiting the GUI leaves it running.
/// </summary>
public partial class App : Application
{
    /// <summary>Developer switch: start in the setup wizard even if the node is already set up.</summary>
    public const string WizardSwitch = "--wizard";

    private AppServices? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = new AppServices();
        var main = new MainViewModel(_services);
        MainWindow = new MainWindow { DataContext = main };
        MainWindow.Show();
        await main.StartAsync(forceWizard: e.Args.Contains(WizardSwitch));
    }

    /// <summary>An update downloaded while the window was open is installed once it closes.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Updater.ApplyOnExit();
        base.OnExit(e);
    }
}
