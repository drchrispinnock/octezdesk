using System.Diagnostics;
using System.Text;

namespace OctezDesk.Core.Hosting;

/// <summary>Runs a local executable with an argv list, capturing (and optionally streaming) its output.</summary>
public static class ProcessRunner
{
    public static async Task<CommandResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? stdin = null,
        Action<string>? onLine = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
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
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var lockObj = new object();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (lockObj) stdout.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (lockObj) stderr.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };

        process.Start();
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
