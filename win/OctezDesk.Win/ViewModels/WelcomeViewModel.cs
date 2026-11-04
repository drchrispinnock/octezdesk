using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctezDesk.Core.Health;
using OctezDesk.Core.Wsl;

namespace OctezDesk.Win.ViewModels;

/// <summary>
/// Wizard screen 1: welcome and the prerequisite checks (Windows version, virtualisation, WSL,
/// a pending restart). Next is enabled only when <see cref="WindowsPrerequisites.Evaluate"/> says
/// setup can go ahead.
/// </summary>
public partial class WelcomeViewModel : ObservableObject
{
    /// <summary>Kept for the life of the app: after "wsl --install", only a restart of Windows clears the need for one.</summary>
    private static bool _installedWslThisSession;

    private readonly MainViewModel _main;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(InstallWslCommand), nameof(CheckCommand))]
    [NotifyPropertyChangedFor(nameof(CanInstallWsl), nameof(ShowActions), nameof(Level))]
    private PrerequisiteResult? _result;

    [ObservableProperty] private string _status = "Checking this PC…";
    [ObservableProperty] private string? _warning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(InstallWslCommand), nameof(CheckCommand))]
    private bool _isBusy;

    public bool CanInstallWsl => Result?.CanInstallWsl == true;

    /// <summary>"Check again" (and "Install WSL…" when it applies) while something still blocks setup.</summary>
    public bool ShowActions => Result is { CanContinue: false };

    /// <summary>Status dot: grey while checking, green when ready, yellow when WSL or a restart is needed, red when this PC can't run it as is.</summary>
    public HealthLevel? Level => Result?.State switch
    {
        null => null,
        PrerequisiteState.Ready => HealthLevel.Green,
        PrerequisiteState.NeedsWsl or PrerequisiteState.NeedsRestart => HealthLevel.Yellow,
        _ => HealthLevel.Red,
    };

    public WelcomeViewModel(MainViewModel main)
    {
        _main = main;
        _ = CheckAsync();
    }

    private bool NotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CheckAsync()
    {
        IsBusy = true;
        Result = null;
        Warning = null;
        Status = "Checking this PC…";
        try
        {
            var facts = await Task.Run(() => WindowsPrerequisites.GatherAsync(_main.Services.Wsl, _installedWslThisSession));
            Result = WindowsPrerequisites.Evaluate(facts);
            Status = Result.Message;
            Warning = Result.Warning;
        }
        catch (Exception ex)
        {
            Status = $"Couldn't check this PC: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private bool CanRunInstall() => CanInstallWsl && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunInstall))]
    private async Task InstallWslAsync()
    {
        IsBusy = true;
        Status = "Installing WSL. Approve the Windows prompt, then wait for the installer to finish…";
        var result = await WslManager.InstallWslElevatedAsync();
        IsBusy = false;
        switch (result.Result)
        {
            case WslInstallResult.Outcome.Declined:
                Status = "WSL was not installed: the Windows prompt was declined. You can try again, or run \"wsl --install\" yourself.";
                return;
            case WslInstallResult.Outcome.Failed:
                Status = $"WSL was not installed. {result.Error}";
                return;
        }
        _installedWslThisSession = true;
        await CheckAsync();
    }

    private bool CanGoNext() => Result?.CanContinue == true && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task Next() => _main.ShowNodeSetupFromWelcomeAsync();
}
