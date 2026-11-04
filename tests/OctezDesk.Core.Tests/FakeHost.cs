using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Tests;

/// <summary>Records commands and answers them from rules; unmatched commands succeed with no output.</summary>
internal sealed class FakeHost : IOctezHost
{
    private readonly List<(Func<IReadOnlyList<string>, bool> Match, Func<CommandResult> Result)> _rules = [];

    public List<(IReadOnlyList<string> Argv, HostUser User, string? StdIn)> Calls { get; } = [];

    public FakeHost On(Func<IReadOnlyList<string>, bool> match, int exitCode = 0, string stdout = "")
    {
        _rules.Insert(0, (match, () => new CommandResult(exitCode, stdout, exitCode == 0 ? "" : "error")));
        return this;
    }

    public FakeHost OnFile(string path, string content)
        => On(a => a is ["cat", var p] && p == path, stdout: content);

    public Task<CommandResult> RunAsync(
        IReadOnlyList<string> argv, HostUser user = HostUser.Root, string? stdin = null,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        Calls.Add((argv, user, stdin));
        foreach (var (match, result) in _rules)
            if (match(argv)) return Task.FromResult(result());
        return Task.FromResult(new CommandResult(0, "", ""));
    }

    public bool Ran(params string[] prefix)
        => Calls.Any(c => c.Argv.Count >= prefix.Length && c.Argv.Take(prefix.Length).SequenceEqual(prefix));
}
