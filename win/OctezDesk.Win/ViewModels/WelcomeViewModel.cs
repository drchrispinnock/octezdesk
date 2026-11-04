using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctezDesk.Core.Wsl;

namespace OctezDesk.Win.ViewModels;

/// <summary>Wizard screen 1: welcome and prerequisite checks.</summary>
public partial class WelcomeViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsWsl))]
    private bool? _wslInstalled;

    [ObservableProperty]
    private string _status = "Checking for the Windows Subsystem for Linux…";

    public bool NeedsWsl => WslInstalled == false;

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
            ? "WSL is installed. OctezDesk will create its own Linux environment for Octez."
            : "OctezDesk runs Octez inside the Windows Subsystem for Linux (WSL), which is not installed yet.";
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
