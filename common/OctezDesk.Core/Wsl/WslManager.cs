using System.ComponentModel;
using System.Diagnostics;
using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Wsl;

/// <param name="Error">Why it failed, for <see cref="Outcome.Failed"/>.</param>
public sealed record WslInstallResult(WslInstallResult.Outcome Result, string? Error = null)
{
    public enum Outcome { Installed, Declined, Failed }

    public static readonly WslInstallResult Installed = new(Outcome.Installed);
    public static readonly WslInstallResult Declined = new(Outcome.Declined);
    public static WslInstallResult Failed(string error) => new(Outcome.Failed, error);
}

/// <summary>Windows-side management of WSL itself and of the OctezDesk distro (wsl.exe verbs).</summary>
public sealed class WslManager
{
    /// <summary>The WSL distribution image we install. Its release is checked after install.</summary>
    public const string BaseImage = "Debian";
    public const string DefaultDistroName = "OctezDesk";

    public static string WslExe => Path.Combine(Environment.SystemDirectory, "wsl.exe");

    /// <summary>Makes wsl.exe's own messages UTF-8 instead of UTF-16LE.</summary>
    public static readonly IReadOnlyDictionary<string, string> Utf8Env =
        new Dictionary<string, string> { ["WSL_UTF8"] = "1" };

    public string DistroName { get; }

    public WslManager(string distroName = DefaultDistroName) => DistroName = distroName;

    private static Task<CommandResult> WslAsync(IEnumerable<string> args, Action<string>? onLine = null, CancellationToken ct = default)
        => ProcessRunner.RunAsync(WslExe, args, onLine: onLine, environment: Utf8Env, ct: ct);

    /// <summary>
    /// True if the Store version of WSL is installed. The inbox stub wsl.exe on a machine without
    /// WSL does not understand --version and exits non-zero.
    /// </summary>
    public async Task<bool> IsWslInstalledAsync(CancellationToken ct = default)
    {
        if (!File.Exists(WslExe)) return false;
        try { return (await WslAsync(["--version"], ct: ct)).Success; }
        catch (Win32Exception) { return false; }
    }

    /// <summary>
    /// Runs <c>wsl --install --no-distribution</c> elevated (UAC prompt). A reboot may be needed afterwards.
    /// Never throws for a failure to start it: the result says what happened.
    /// </summary>
    public static async Task<WslInstallResult> InstallWslElevatedAsync(CancellationToken ct = default)
    {
        if (!File.Exists(WslExe))
            return WslInstallResult.Failed(
                "This copy of Windows doesn't include wsl.exe. Update Windows (Windows 10 version 2004 or later, or Windows 11), then try again.");
        var psi = new ProcessStartInfo(WslExe, "--install --no-distribution")
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0
                ? WslInstallResult.Installed
                : WslInstallResult.Failed($"\"wsl --install\" stopped with exit code {p.ExitCode}. Try running \"wsl --install\" yourself to see why.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC declined
        {
            return WslInstallResult.Declined;
        }
        catch (Win32Exception ex)
        {
            return WslInstallResult.Failed($"Couldn't start the WSL installer: {ex.Message}");
        }
    }

    public Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct = default)
        => ListAsync(["--list", "--quiet"], ct);

    /// <summary>Distros that are running now. Empty when none are (wsl.exe then exits non-zero).</summary>
    public Task<IReadOnlyList<string>> ListRunningDistrosAsync(CancellationToken ct = default)
        => ListAsync(["--list", "--running", "--quiet"], ct);

    private static async Task<IReadOnlyList<string>> ListAsync(string[] args, CancellationToken ct)
    {
        var r = await WslAsync(args, ct: ct);
        if (!r.Success) return [];
        return r.StdOut
            .Split('\n')
            .Select(l => l.Trim().Trim('\0'))
            .Where(l => l.Length > 0)
            .ToList();
    }

    public async Task<bool> DistroExistsAsync(CancellationToken ct = default)
        => (await ListDistrosAsync(ct)).Contains(DistroName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Installs <see cref="BaseImage"/> as a distro named <see cref="DistroName"/> with its VHDX in <paramref name="location"/>.</summary>
    public async Task InstallDistroAsync(string location, Action<string>? onLine = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(location);
        (await WslAsync(["--install", BaseImage, "--name", DistroName, "--location", location, "--no-launch"], onLine, ct))
            .EnsureSuccess($"Installing WSL distro {DistroName}");
    }

    /// <summary>Stops WSL's VM and every distro in it, so that .wslconfig changes take effect.</summary>
    public static async Task ShutdownAsync(CancellationToken ct = default)
        => (await WslAsync(["--shutdown"], ct: ct)).EnsureSuccess("wsl --shutdown");

    public async Task TerminateAsync(CancellationToken ct = default)
        => await WslAsync(["--terminate", DistroName], ct: ct);

    /// <summary>Deletes the distro and everything in it, including the node's data. Callers must confirm with the user.</summary>
    public async Task UnregisterAsync(CancellationToken ct = default)
        => (await WslAsync(["--unregister", DistroName], ct: ct)).EnsureSuccess($"Removing WSL distro {DistroName}");
}
