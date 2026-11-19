using System.Text.RegularExpressions;

namespace Relctl;

/// <summary>
/// Edits an environment manifest as text, line by line, so comments and layout survive. A YAML
/// round trip would drop every comment, and comments are often the most useful part of a
/// release file ("held back until the partner is ready").
/// </summary>
public static partial class ManifestEditor
{
    [GeneratedRegex(@"^(?<indent>\s+)(?<name>[a-z][a-z0-9-]*)\s*:(?<rest>.*)$")]
    private static partial Regex EntryLine();

    [GeneratedRegex(@"^components\s*:\s*(#.*)?$")]
    private static partial Regex ComponentsLine();

    // `  1.2.3     # comment` → `     # comment`, spacing kept so columns stay lined up.
    [GeneratedRegex(@"^\s*[^\s{#]+(?<comment>\s+#.*)$")]
    private static partial Regex TrailingComment();

    [GeneratedRegex(@"^release\s*:.*$")]
    private static partial Regex ReleaseLine();

    /// <summary>Set one component's version, replacing any `{ version, rollback }` form with a plain version.</summary>
    public static string SetVersion(string text, string component, SemVer version)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n').ToList();
        var start = lines.FindIndex(l => ComponentsLine().IsMatch(l));
        if (start < 0)
        {
            // No components block yet: add one at the end.
            if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
            lines.Add("components:");
            lines.Add($"  {component}: {version}");
            return string.Join('\n', lines) + "\n";
        }

        // The block is every following line that is indented, blank, or a comment.
        var end = start + 1;
        while (end < lines.Count && (lines[end].Length == 0 || char.IsWhiteSpace(lines[end][0]) || lines[end].TrimStart().StartsWith('#'))) end++;
        // Don't swallow trailing blank lines or comments that belong to the next section.
        while (end > start + 1 && (lines[end - 1].Trim().Length == 0 || (lines[end - 1].Length > 0 && !char.IsWhiteSpace(lines[end - 1][0])))) end--;

        string? indent = null;
        for (var i = start + 1; i < end; i++)
        {
            var m = EntryLine().Match(lines[i]);
            if (!m.Success) continue;
            indent ??= m.Groups["indent"].Value;
            if (m.Groups["indent"].Value != indent || m.Groups["name"].Value != component) continue;

            // Keep a trailing comment on a simple `name: 1.2.3  # comment` line.
            var comment = TrailingComment().Match(m.Groups["rest"].Value) is { Success: true } c ? c.Groups["comment"].Value : "";
            lines[i] = $"{indent}{component}: {version}{comment}";
            // Remove the nested lines of a block-style entry (version: / rollback: on their own lines).
            var j = i + 1;
            while (j < end && lines[j].Length > indent.Length && lines[j].StartsWith(indent, StringComparison.Ordinal) && char.IsWhiteSpace(lines[j][indent.Length]))
                j++;
            lines.RemoveRange(i + 1, j - i - 1);
            return string.Join('\n', lines);
        }

        lines.Insert(end, $"{indent ?? "  "}{component}: {version}");
        return string.Join('\n', lines);
    }

    public static string SetRelease(string text, string release)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n').ToList();
        var i = lines.FindIndex(l => ReleaseLine().IsMatch(l));
        if (i >= 0) lines[i] = $"release: {release}";
        else
        {
            var env = lines.FindIndex(l => l.StartsWith("environment:", StringComparison.Ordinal));
            lines.Insert(env + 1, $"release: {release}");
        }
        return string.Join('\n', lines);
    }

    public sealed record Change(string Component, SemVer? From, SemVer To);

    /// <summary>
    /// Make `to` match `from` for the given components (all by default). The PR this produces is
    /// the release: its diff is exactly what will change.
    /// </summary>
    public static (string Text, List<Change> Changes, List<string> Skipped) Promote(
        ReleaseRepo repo, string from, string to, string toText, IReadOnlyCollection<string>? only, string? release)
    {
        var source = repo.Env(from);
        var target = repo.Env(to);
        var changes = new List<Change>();
        var skipped = new List<string>();
        var text = toText;

        foreach (var wanted in only ?? [])
            if (!source.Components.ContainsKey(wanted))
                throw new RelctlException($"{wanted} isn't in {from}, so it can't be promoted from there.");

        foreach (var c in source.Components.Values.OrderBy(c => c.Name))
        {
            if (only is { Count: > 0 } && !only.Contains(c.Name)) continue;
            if (repo.Catalog.TryGetValue(c.Name, out var component) && !component.Targets.ContainsKey(to))
            {
                skipped.Add($"{c.Name} (no {to} target in {component.File})");
                continue;
            }
            var current = target.Components.GetValueOrDefault(c.Name);
            if (current?.Version == c.Version && current.Rollback is null) continue;
            text = SetVersion(text, c.Name, c.Version);
            changes.Add(new Change(c.Name, current?.Version, c.Version));
        }

        // The release name travels with the versions, so "2026.11.3" means the same set everywhere.
        var releaseName = release ?? source.Release;
        if (changes.Count > 0 && releaseName is not null && releaseName != target.Release) text = SetRelease(text, releaseName);
        return (text, changes, skipped);
    }
}
