namespace OctezDesk.Core.Octez;

/// <summary>
/// Minimal reader for <c>/etc/default/*</c> files: <c>KEY=value</c> lines, optional single or
/// double quotes, <c>#</c> comments, optional leading <c>export</c>. No variable expansion.
/// </summary>
public static class ShellDefaultsFile
{
    public static IReadOnlyDictionary<string, string> Parse(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            else
            {
                var hash = value.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0) value = value[..hash].TrimEnd();
            }
            result[key] = value;
        }
        return result;
    }
}
