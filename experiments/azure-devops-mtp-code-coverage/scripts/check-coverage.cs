#!/usr/bin/env dotnet
// Per-project coverage gate. A .NET 10 file-based app: `dotnet run scripts/check-coverage.cs -- ...`.
//
// Microsoft.Testing.Extensions.CodeCoverage collects coverage but has no "fail under N%" option,
// so this does that one job. For every test project under --tests it reads that project's own
// testconfig.json:
//
//   codeCoverage.Configuration.CoverageFileName -> which Cobertura report in --results is this project's
//   coverageGate.minimumLineRate / minimumBranchRate -> the bar that report has to clear
//
// so the thresholds live next to the coverage settings they apply to, one file per project.
//
// Usage:
//   dotnet run scripts/check-coverage.cs -- --results <dir> [--tests <dir>] [--summary <file.md>]
//
// --summary writes a Markdown table. On Azure DevOps (TF_BUILD set) it is also attached to the run
// summary page and every failure is logged as a pipeline error; on GitHub Actions point it at
// $GITHUB_STEP_SUMMARY. Exit code 1 if any project is under its bar or a report is missing.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

var options = ParseArgs(args);
if (!options.TryGetValue("results", out var resultsDir))
{
    Console.Error.WriteLine("usage: check-coverage.cs --results <dir> [--tests <dir>] [--summary <file.md>]");
    return 2;
}

var testsDir = options.GetValueOrDefault("tests") ?? "tests";
var onAzureDevOps = Environment.GetEnvironmentVariable("TF_BUILD") is not null;

var configs = Directory.GetFiles(testsDir, "testconfig.json", SearchOption.AllDirectories)
    .Where(p => !p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj"))
    .Order(StringComparer.Ordinal)
    .ToList();

if (configs.Count == 0)
{
    Console.Error.WriteLine($"No testconfig.json found under {Path.GetFullPath(testsDir)}.");
    return 2;
}

var rows = new List<Row>();
foreach (var configPath in configs)
{
    var project = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(configPath)))!;
    rows.Add(Evaluate(project, configPath, resultsDir));
}

var table = new StringBuilder()
    .AppendLine("## Code coverage per test project")
    .AppendLine()
    .AppendLine("| Project | Line | Min line | Branch | Min branch | Result |")
    .AppendLine("|---|---:|---:|---:|---:|---|");
foreach (var r in rows)
{
    table.AppendLine(CultureInfo.InvariantCulture,
        $"| {r.Project} | {Pct(r.Line)} | {Pct(r.MinLine)} | {Pct(r.Branch)} | {Pct(r.MinBranch)} | {(r.Passed ? "✅ pass" : "❌ " + r.Problem)} |");
}

Console.WriteLine(table);

if (options.TryGetValue("summary", out var summaryPath))
{
    File.AppendAllText(summaryPath, table.ToString());
    if (onAzureDevOps)
    {
        Console.WriteLine($"##vso[task.uploadsummary]{Path.GetFullPath(summaryPath)}");
    }
}

foreach (var failed in rows.Where(r => !r.Passed))
{
    var message = $"{failed.Project}: {failed.Problem}";
    Console.WriteLine(onAzureDevOps ? $"##vso[task.logissue type=error]{message}" : $"FAIL {message}");
}

return rows.All(r => r.Passed) ? 0 : 1;

static Row Evaluate(string project, string configPath, string resultsDir)
{
    JsonElement root;
    try
    {
        // testconfig.json allows // comments and trailing commas, so the reader has to as well.
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        root = doc.RootElement.Clone();
    }
    catch (JsonException ex)
    {
        return Row.Fail(project, $"{configPath} is not valid JSON: {ex.Message}");
    }

    var fileName = TryGet(root, "codeCoverage", "Configuration", "CoverageFileName")?.GetString();
    if (string.IsNullOrWhiteSpace(fileName))
    {
        return Row.Fail(project, "testconfig.json has no codeCoverage.Configuration.CoverageFileName");
    }

    var format = TryGet(root, "codeCoverage", "Configuration", "Format")?.GetString();
    if (!string.Equals(format, "cobertura", StringComparison.OrdinalIgnoreCase))
    {
        return Row.Fail(project, $"codeCoverage.Configuration.Format must be cobertura for the gate (is '{format ?? "coverage (default)"}')");
    }

    var minLine = TryGet(root, "coverageGate", "minimumLineRate")?.GetDouble();
    var minBranch = TryGet(root, "coverageGate", "minimumBranchRate")?.GetDouble();
    if (minLine is null && minBranch is null)
    {
        return Row.Fail(project, "testconfig.json has no coverageGate thresholds");
    }

    var reportPath = Path.IsPathRooted(fileName) ? fileName : Path.Combine(resultsDir, fileName);
    if (!File.Exists(reportPath))
    {
        return Row.Fail(project, $"no coverage report at {reportPath} (was the run started with --coverage?)") with
        {
            MinLine = minLine,
            MinBranch = minBranch,
        };
    }

    var coverage = XDocument.Load(reportPath).Root!;
    var line = Rate(coverage, "line-rate");
    var branch = Rate(coverage, "branch-rate");

    var problems = new List<string>();
    if (minLine is { } l && line < l)
    {
        problems.Add($"line coverage {Pct(line)} is below {Pct(l)}");
    }

    if (minBranch is { } b && branch < b)
    {
        problems.Add($"branch coverage {Pct(branch)} is below {Pct(b)}");
    }

    return new Row(project, line, minLine, branch, minBranch, problems.Count == 0, string.Join("; ", problems));
}

// Cobertura stores rates as 0..1; thresholds in testconfig.json are percentages.
static double Rate(XElement coverage, string attribute) =>
    double.Parse(coverage.Attribute(attribute)?.Value ?? "0", CultureInfo.InvariantCulture) * 100;

static JsonElement? TryGet(JsonElement element, params string[] path)
{
    foreach (var key in path)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out element))
        {
            return null;
        }
    }

    return element;
}

static string Pct(double? value) => value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            result[args[i][2..]] = args[++i];
        }
    }

    return result;
}

internal sealed record Row(string Project, double? Line, double? MinLine, double? Branch, double? MinBranch, bool Passed, string Problem)
{
    public static Row Fail(string project, string problem) => new(project, null, null, null, null, false, problem);
}
