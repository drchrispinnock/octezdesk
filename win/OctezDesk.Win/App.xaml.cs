using System.Windows;
using OctezDesk.Core;
using OctezDesk.Core.Health;
using OctezDesk.Core.Octez;
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

    public AppServices()
    {
        Host = new WslHost(Wsl.DistroName);
        Node = new NodeService(Host);
        Monitor = new NodeMonitor(Host, Node);
        Probe = new InstallationProbe(Wsl, Node);
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var main = new MainViewModel(new AppServices());
        MainWindow = new MainWindow { DataContext = main };
        MainWindow.Show();
        await main.StartAsync(forceWizard: e.Args.Contains(WizardSwitch));
    }
}
