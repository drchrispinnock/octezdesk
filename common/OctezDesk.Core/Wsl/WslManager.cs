using System.ComponentModel;
using System.Diagnostics;
using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Wsl;

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
    /// Returns false if the user declined the UAC prompt.
    /// </summary>
    public static async Task<bool> InstallWslElevatedAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(WslExe, "--install --no-distribution")
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC declined
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct = default)
    {
        var r = await WslAsync(["--list", "--quiet"], ct: ct);
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

    public async Task TerminateAsync(CancellationToken ct = default)
        => await WslAsync(["--terminate", DistroName], ct: ct);

    /// <summary>Deletes the distro and everything in it, including the node's data. Callers must confirm with the user.</summary>
    public async Task UnregisterAsync(CancellationToken ct = default)
        => (await WslAsync(["--unregister", DistroName], ct: ct)).EnsureSuccess($"Removing WSL distro {DistroName}");
}
