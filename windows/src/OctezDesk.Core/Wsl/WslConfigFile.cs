namespace OctezDesk.Core.Wsl;

/// <summary>
/// Reads and edits the user's global WSL settings file, <c>%UserProfile%\.wslconfig</c> (INI format).
/// It applies to every distro, so edits are minimal and keep everything else in the file as it was.
/// </summary>
public static class WslConfigFile
{
    public const string Mirrored = "mirrored";
    private const string Section = "wsl2";
    private const string Key = "networkingMode";

    public static string DefaultPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");

    /// <summary>The <c>[wsl2] networkingMode</c> value, or null if it isn't set.</summary>
    public static string? GetNetworkingMode(string? content)
    {
        string? section = null;
        foreach (var raw in Lines(content))
        {
            var line = raw.Trim();
            if (TryParseSection(line, out var s)) { section = s; continue; }
            if (IsSection(section) && TryParseKey(line, out var key, out var value) && IsKey(key))
                return value;
        }
        return null;
    }

    /// <summary>
    /// Returns <paramref name="content"/> with <c>networkingMode=mirrored</c> under <c>[wsl2]</c>,
    /// adding the section if needed. Callers decide beforehand whether to override an existing value.
    /// </summary>
    public static string WithMirroredNetworking(string? content)
    {
        var lines = Lines(content).ToList();
        var newline = content?.Contains("\r\n") == true || string.IsNullOrEmpty(content) ? "\r\n" : "\n";
        string? section = null;
        var sectionEnd = -1; // index after the last line of the [wsl2] section

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (TryParseSection(line, out var s)) { section = s; continue; }
            if (!IsSection(section)) continue;
            if (TryParseKey(line, out var key, out _) && IsKey(key))
            {
                lines[i] = $"{Key}={Mirrored}";
                return Join(lines, newline);
            }
            if (line.Length > 0) sectionEnd = i + 1;
        }

        var header = lines.FindIndex(l => TryParseSection(l.Trim(), out var s) && IsSection(s));
        if (header >= 0)
        {
            lines.Insert(Math.Max(sectionEnd, header + 1), $"{Key}={Mirrored}");
        }
        else
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) lines.Add("");
            lines.Add($"[{Section}]");
            lines.Add($"{Key}={Mirrored}");
        }
        return Join(lines, newline);
    }

    private static IEnumerable<string> Lines(string? content)
    {
        if (string.IsNullOrEmpty(content)) return [];
        var lines = content.Replace("\r\n", "\n").Split('\n');
        // A trailing newline gives an empty last element; Join adds it back.
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static string Join(List<string> lines, string newline) => string.Join(newline, lines) + newline;

    private static bool TryParseSection(string line, out string name)
    {
        name = "";
        if (line.Length < 2 || line[0] != '[') return false;
        var end = line.IndexOf(']');
        if (end < 0) return false;
        name = line[1..end].Trim();
        return true;
    }

    private static bool TryParseKey(string line, out string key, out string value)
    {
        key = value = "";
        if (line.Length == 0 || line[0] is '#' or ';') return false;
        var eq = line.IndexOf('=');
        if (eq <= 0) return false;
        key = line[..eq].Trim();
        value = line[(eq + 1)..];
        var comment = value.IndexOfAny(['#', ';']);
        if (comment >= 0) value = value[..comment];
        value = value.Trim().Trim('"');
        return true;
    }

    private static bool IsSection(string? name) => string.Equals(name, Section, StringComparison.OrdinalIgnoreCase);
    private static bool IsKey(string key) => string.Equals(key, Key, StringComparison.OrdinalIgnoreCase);
}
