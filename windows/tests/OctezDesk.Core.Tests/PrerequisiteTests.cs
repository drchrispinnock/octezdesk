using OctezDesk.Core.Wsl;

namespace OctezDesk.Core.Tests;

public class PrerequisiteTests
{
    private static WindowsFacts Ok => new(26100, WslInstalled: true, VirtualizationEnabled: true, RestartPending: false, InstalledWslThisSession: false);

    [Fact]
    public void Ready_when_everything_is_in_place()
    {
        var r = WindowsPrerequisites.Evaluate(Ok);
        Assert.Equal(PrerequisiteState.Ready, r.State);
        Assert.True(r.CanContinue);
        Assert.Null(r.Warning);
    }

    [Fact]
    public void Old_windows_is_unsupported()
        => Assert.Equal(PrerequisiteState.UnsupportedWindows, WindowsPrerequisites.Evaluate(Ok with { WindowsBuild = 18363 }).State);

    [Fact]
    public void Virtualisation_off_blocks_even_with_wsl_installed()
    {
        var r = WindowsPrerequisites.Evaluate(Ok with { VirtualizationEnabled = false });
        Assert.Equal(PrerequisiteState.VirtualizationOff, r.State);
        Assert.False(r.CanContinue);
        Assert.False(r.CanInstallWsl);
    }

    [Fact]
    public void Unknown_virtualisation_doesnt_block()
        => Assert.True(WindowsPrerequisites.Evaluate(Ok with { VirtualizationEnabled = null }).CanContinue);

    [Fact]
    public void Missing_wsl_offers_the_install()
    {
        var r = WindowsPrerequisites.Evaluate(Ok with { WslInstalled = false });
        Assert.Equal(PrerequisiteState.NeedsWsl, r.State);
        Assert.True(r.CanInstallWsl);
    }

    [Theory]
    [InlineData(true, true)]   // installed, Windows wants a restart
    [InlineData(false, false)] // installed, but wsl --version doesn't work until the restart
    public void After_installing_wsl_a_restart_is_needed(bool wslWorks, bool restartPending)
    {
        var r = WindowsPrerequisites.Evaluate(Ok with { WslInstalled = wslWorks, RestartPending = restartPending, InstalledWslThisSession = true });
        Assert.Equal(PrerequisiteState.NeedsRestart, r.State);
        Assert.False(r.CanContinue);
    }

    [Fact]
    public void An_unrelated_pending_restart_only_warns()
    {
        var r = WindowsPrerequisites.Evaluate(Ok with { RestartPending = true });
        Assert.True(r.CanContinue);
        Assert.NotNull(r.Warning);
    }

    [Theory]
    [InlineData("True False", true)]
    [InlineData("False True", true)]  // a running hypervisor hides the firmware flag
    [InlineData("False False", false)]
    [InlineData(" False", false)]
    [InlineData("", null)]
    [InlineData("garbage", null)]
    public void Reads_virtualisation(string output, bool? expected)
        => Assert.Equal(expected, WindowsPrerequisites.ParseVirtualization(output));

    [Theory]
    [InlineData("Error code: Wsl/InstallDistro/Service/RegisterDistro/CreateVm/HCS/HCS_E_HYPERV_NOT_INSTALLED", "virtualisation")]
    [InlineData("WslRegisterDistribution failed with error: 0x80370102", "virtualisation")]
    [InlineData("Error code: Wsl/InstallDistro/WININET_E_NAME_NOT_RESOLVED", "internet")]
    public void Explains_known_wsl_errors(string output, string mentions)
        => Assert.Contains(mentions, WslErrors.Explain(output));

    [Fact]
    public void Unknown_wsl_errors_are_left_alone() => Assert.Null(WslErrors.Explain("Something else went wrong"));
}
