using System.Diagnostics;
using Microsoft.Win32;
using OctezDesk.Core.Wsl;

namespace OctezDesk.Win;

/// <summary>
/// Keeps the OctezDesk distro (and so the node) running independently of the GUI.
/// WSL stops a distro ~20 s after no Windows process is attached (confirmed on WSL 2.7.3), so a
/// windowless helper — this same exe started with <see cref="Switch"/> — holds it open. The GUI
/// starts it when needed, so exiting the GUI never stops the node. Starting it at logon (HKCU Run
/// key) is opt-in: the user turns it on from the menu; we never register it silently.
/// </summary>
public static class KeepAliveService
{
    public const string Switch = "--keep-alive";
    private const string MutexName = @"Local\OctezDesk.KeepAlive";
    private const string StopEventName = @"Local\OctezDesk.KeepAlive.Stop";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "OctezDesk";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>True if a keep-alive helper is running in this Windows session.</summary>
    public static bool IsRunning()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>Starts the helper if it is not running.</summary>
    public static void EnsureRunning()
    {
        if (IsRunning()) return;
        var psi = new ProcessStartInfo(Environment.ProcessPath!, Switch) { UseShellExecute = false, CreateNoWindow = true };
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Asks a running helper to exit (e.g. before uninstalling). The node then stops once WSL idles the distro.</summary>
    public static void RequestStop()
    {
        if (EventWaitHandle.TryOpenExisting(StopEventName, out var stop))
            using (stop) stop.Set();
    }

    public static bool IsAutostartRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string;
    }

    /// <summary>Only call in response to the user turning on "Keep node running after sign-in".</summary>
    public static void RegisterAutostart()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        var command = $"\"{Environment.ProcessPath}\" {Switch}";
        if (key.GetValue(RunValueName) as string != command) key.SetValue(RunValueName, command);
    }

    public static void UnregisterAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Helper-process entry point. Holds the distro open, re-attaching if WSL stops it
    /// (e.g. <c>wsl --shutdown</c>). Exits if another helper is already running, if the distro
    /// no longer exists, or when <see cref="RequestStop"/> is called.
    /// </summary>
    public static int Run()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew) return 0;
        try
        {
            using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
            var wsl = new WslManager();
            using var holder = new WslKeepAlive(wsl.DistroName);
            do
            {
                if (holder.IsRunning) continue;
                if (!wsl.DistroExistsAsync().GetAwaiter().GetResult()) return 0;
                holder.EnsureRunning();
            }
            while (!stop.WaitOne(CheckInterval));
            return 0;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
