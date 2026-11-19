using System.Diagnostics;
using System.Text;

namespace OctezDesk.Core.Hosting;

/// <summary>Runs a local executable with an argv list, capturing (and optionally streaming) its output.</summary>
public static class ProcessRunner
{
    private static readonly object JobLock = new();
    /// <summary>A <c>KillOnCloseJob</c> holding the children started with <c>endsWithApp</c>; Windows closes it, and kills them, when this process ends.</summary>
    private static object? _appJob;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void EndWithApp(Process process)
    {
        try
        {
            lock (JobLock) ((Wsl.KillOnCloseJob)(_appJob ??= new Wsl.KillOnCloseJob())).Add(process);
        }
        catch (System.ComponentModel.Win32Exception) { } // already exited, or can't be added: it ends with its own command
    }

    /// <param name="endsWithApp">
    /// Kill the process if this app exits or is killed before it finishes. For commands that never
    /// end by themselves, like following a log; not for setup work, which should finish even if the
    /// window is closed.
    /// </param>
    public static async Task<CommandResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? stdin = null,
        Action<string>? onLine = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default,
        bool endsWithApp = false)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            // Never the app's own folder (the default, inherited): a child still running there, such
            // as an orphaned wsl.exe, stops Velopack from replacing that folder, so updates fail.
            WorkingDirectory = OperatingSystem.IsWindows() ? Environment.SystemDirectory : "/",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi };
        // Lines are joined with LF, not Environment.NewLine: the output is mostly Linux text (e.g. a file
        // read with cat) that callers compare with what they wrote.
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var lockObj = new object();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (lockObj) stdout.Append(e.Data).Append('\n');
            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (lockObj) stderr.Append(e.Data).Append('\n');
            onLine?.Invoke(e.Data);
        };

        process.Start();
        if (endsWithApp && OperatingSystem.IsWindows()) EndWithApp(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            // Write raw UTF-8 without BOM; Linux tools don't want one.
            var bytes = new UTF8Encoding(false).GetBytes(stdin);
            await process.StandardInput.BaseStream.WriteAsync(bytes, ct);
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        // Ensure the async readers have drained.
        process.WaitForExit();
        lock (lockObj)
            return new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
