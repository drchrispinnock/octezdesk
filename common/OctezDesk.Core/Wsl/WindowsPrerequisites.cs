using System.Runtime.Versioning;
using Microsoft.Win32;
using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Wsl;

/// <summary>What the Welcome screen checks on Windows before setup.</summary>
/// <param name="WindowsBuild">e.g. 26100.</param>
/// <param name="VirtualizationEnabled">Null if it couldn't be determined; then it doesn't block.</param>
/// <param name="RestartPending">Windows is waiting for a restart (e.g. after enabling a Windows feature).</param>
/// <param name="InstalledWslThisSession">OctezDesk ran <c>wsl --install</c> since it started, so a restart is due.</param>
public sealed record WindowsFacts(
    int WindowsBuild, bool WslInstalled, bool? VirtualizationEnabled, bool RestartPending, bool InstalledWslThisSession);

public enum PrerequisiteState
{
    UnsupportedWindows,
    VirtualizationOff,
    NeedsRestart,
    NeedsWsl,
    Ready,
}

/// <param name="Warning">Shown alongside a state that doesn't block, e.g. an unrelated pending restart.</param>
public sealed record PrerequisiteResult(PrerequisiteState State, string Message, string? Warning = null)
{
    public bool CanContinue => State == PrerequisiteState.Ready;
    public bool CanInstallWsl => State == PrerequisiteState.NeedsWsl;
}

public static class WindowsPrerequisites
{
    /// <summary>Windows 10 version 2004: the first with <c>wsl --install</c> and WSL 2 from the Store.</summary>
    public const int MinimumBuild = 19041;

    public static PrerequisiteResult Evaluate(WindowsFacts f)
    {
        if (f.WindowsBuild < MinimumBuild)
            return new(PrerequisiteState.UnsupportedWindows,
                $"OctezDesk needs Windows 10 version 2004 (build {MinimumBuild}) or later, or Windows 11. This PC runs build {f.WindowsBuild}. " +
                "Update Windows, then open OctezDesk again.");

        if (f.VirtualizationEnabled == false)
            return new(PrerequisiteState.VirtualizationOff,
                "Hardware virtualisation is turned off, and WSL needs it to run Linux. Turn it on in your PC's firmware (UEFI or BIOS) settings, " +
                "where it may be called Intel Virtualization Technology (VT-x), AMD-V or SVM Mode, then start Windows again.");

        if (f.InstalledWslThisSession && (f.RestartPending || !f.WslInstalled))
            return new(PrerequisiteState.NeedsRestart, "WSL is installed. Restart Windows, then open OctezDesk again to continue.");

        if (!f.WslInstalled)
            return new(PrerequisiteState.NeedsWsl,
                "Octez runs inside WSL, which isn't installed yet. Installing it needs administrator approval and may need a restart.",
                f.RestartPending ? "Windows is waiting for a restart. If you've just installed WSL yourself, restart first." : null);

        return new(PrerequisiteState.Ready,
            "Ready: WSL is installed and virtualisation is on. OctezDesk will create its own Linux environment and won't touch any others you have.",
            f.RestartPending
                ? "Windows is waiting for a restart. If you installed WSL or turned on Windows features recently, restart before setting up, " +
                  "or creating the Linux environment may fail."
                : null);
    }

    /// <summary>
    /// Reads "<c>VirtualizationFirmwareEnabled HypervisorPresent</c>" (e.g. "False True"). While a
    /// hypervisor is running, Windows reports firmware virtualisation as False, so a running
    /// hypervisor counts as enabled. Null if neither value could be read.
    /// </summary>
    public static bool? ParseVirtualization(string output)
    {
        var parts = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        static bool? Read(string[] p, int i) => i < p.Length && bool.TryParse(p[i], out var b) ? b : null;
        var firmware = Read(parts, 0);
        var hypervisor = Read(parts, 1);
        if (hypervisor == true || firmware == true) return true;
        return firmware;
    }

    private const string VirtualizationScript =
        "$p = (Get-CimInstance Win32_Processor | Select-Object -First 1).VirtualizationFirmwareEnabled; " +
        "$h = (Get-CimInstance Win32_ComputerSystem).HypervisorPresent; \"$p $h\"";

    public static async Task<WindowsFacts> GatherAsync(WslManager wsl, bool installedWslThisSession, CancellationToken ct = default)
    {
        var wslInstalled = await wsl.IsWslInstalledAsync(ct);
        bool? virtualization = null;
        try
        {
            var r = await ProcessRunner.RunAsync(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-Command", VirtualizationScript], ct: ct);
            if (r.Success) virtualization = ParseVirtualization(r.StdOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { } // unknown: don't block
        return new WindowsFacts(
            Environment.OSVersion.Version.Build, wslInstalled, virtualization,
            OperatingSystem.IsWindows() && IsRestartPending(), installedWslThisSession);
    }

    /// <summary>The keys Windows sets while a restart is due to finish installing features or updates.</summary>
    [SupportedOSPlatform("windows")]
    private static bool IsRestartPending()
    {
        try
        {
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return cbs is not null || wu is not null;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>Plain-language explanations for WSL errors seen when creating a distro.</summary>
public static class WslErrors
{
    private static readonly (string[] Codes, string Explanation)[] Known =
    [
        (["0x80370102", "HCS_E_HYPERV_NOT_INSTALLED"],
            "WSL couldn't start its virtual machine. Turn on hardware virtualisation in your PC's firmware (UEFI or BIOS) settings, " +
            "and make sure the Virtual Machine Platform feature is on (run \"wsl --install --no-distribution\" as administrator), then restart Windows."),
        (["0x8007019e", "WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED"],
            "The Windows Subsystem for Linux feature isn't turned on yet. Install WSL from the Welcome screen, or restart Windows if you just did."),
        (["0x80370114", "HCS_E_SERVICE_NOT_AVAILABLE"],
            "A Windows virtualisation service isn't running. Restart Windows, then retry."),
        (["0x800701bc"],
            "WSL needs an update. Run \"wsl --update\", then retry."),
        (["0x80072ee7", "0x80072efd", "0x80072ee2", "WININET_E_NAME_NOT_RESOLVED", "WININET_E_CANNOT_CONNECT", "WININET_E_TIMEOUT"],
            "WSL couldn't download Debian. Check this PC's internet connection, then retry."),
    ];

    /// <summary>An explanation for the first known error code in <paramref name="text"/>, or null.</summary>
    public static string? Explain(string text)
        => Known.FirstOrDefault(k => k.Codes.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase))).Explanation;
}
