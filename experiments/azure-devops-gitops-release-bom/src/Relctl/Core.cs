using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Relctl;

/// <summary>
/// An exact release version: major.minor.patch, nothing else. Pre-release tags are deliberately
/// not allowed. Anything named in a manifest has been built from main and published, so it is
/// a release by definition.
/// </summary>
public readonly partial record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, [NotNullWhen(true)] out SemVer? version)
    {
        version = null;
        var m = text is null ? null : Pattern().Match(text);
        if (m is null || !m.Success) return false;
        version = new SemVer(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        return true;
    }

    public static SemVer Parse(string text) =>
        TryParse(text, out var v) ? v.Value : throw new FormatException($"\"{text}\" isn't an exact x.y.z version.");

    public int CompareTo(SemVer other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public enum Severity { Error, Warning }

public sealed record Diagnostic(Severity Severity, string File, int Line, string Message)
{
    public override string ToString() => Line > 0 ? $"{File}:{Line}: {Message}" : $"{File}: {Message}";
}

/// <summary>Collects every problem found, so one run reports all of them, not just the first.</summary>
public sealed class Diagnostics
{
    private readonly List<Diagnostic> _items = [];
    public IReadOnlyList<Diagnostic> Items => _items;
    public IEnumerable<Diagnostic> Errors => _items.Where(d => d.Severity == Severity.Error);
    public IEnumerable<Diagnostic> Warnings => _items.Where(d => d.Severity == Severity.Warning);
    public bool HasErrors => _items.Any(d => d.Severity == Severity.Error);

    public void Error(string file, int line, string message) => _items.Add(new(Severity.Error, file, line, message));
    public void Warning(string file, int line, string message) => _items.Add(new(Severity.Warning, file, line, message));
}

/// <summary>Thrown for problems that stop a command outright (bad arguments, a failed deploy).</summary>
public sealed class RelctlException(string message) : Exception(message);
