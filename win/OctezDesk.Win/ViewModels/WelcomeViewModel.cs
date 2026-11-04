using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctezDesk.Core.Health;
using OctezDesk.Core.Wsl;

namespace OctezDesk.Win.ViewModels;

/// <summary>Wizard screen 1: welcome and prerequisite checks.</summary>
public partial class WelcomeViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsWsl), nameof(WslLevel))]
    private bool? _wslInstalled;

    [ObservableProperty]
    private string _status = "Checking for the Windows Subsystem for Linux…";

    public bool NeedsWsl => WslInstalled == false;

    /// <summary>Status dot: grey while checking, green when installed, yellow when missing.</summary>
    public HealthLevel? WslLevel => WslInstalled switch
    {
        true => HealthLevel.Green,
        false => HealthLevel.Yellow,
        null => null,
    };

    public WelcomeViewModel(MainViewModel main)
    {
        _main = main;
        _ = CheckAsync();
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        WslInstalled = null;
        Status = "Checking for the Windows Subsystem for Linux…";
        WslInstalled = await _main.Services.Wsl.IsWslInstalledAsync();
        Status = WslInstalled == true
            ? "Installed. OctezDesk will create its own Linux environment and won't touch any others you have."
            : "Octez runs inside WSL, which isn't installed yet. Installing it needs administrator approval and may need a restart.";
    }

    [RelayCommand]
    private async Task InstallWslAsync()
    {
        Status = "Installing WSL — approve the Windows prompt…";
        if (!await WslManager.InstallWslElevatedAsync())
        {
            Status = "WSL was not installed. You can try again, or run \"wsl --install\" yourself.";
            return;
        }
        await CheckAsync();
        if (WslInstalled != true)
            Status = "WSL has been installed. Restart Windows, then open OctezDesk again to continue.";
    }

    private bool CanGoNext() => WslInstalled == true;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => _main.ShowNodeSetup();
}
