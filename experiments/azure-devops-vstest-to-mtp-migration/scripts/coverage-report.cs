#!/usr/bin/env dotnet
// Per-project coverage table for an MTP run with coverlet.MTP.
// A .NET 10 file-based app: `dotnet run scripts/coverage-report.cs -- --results <dir> [--tests tests] [--summary file.md]`
//
// coverlet.MTP enforces each project's threshold itself (from testconfig.json), but under
// `dotnet test` a failure only shows up as "exit code 14": the "Coverage Threshold Results" table
// the test app prints is not passed through. This prints the same comparison, per project:
//
//   threshold, thresholdType, thresholdStat  <- tests/<Project>/testconfig.json, platformOptions.Coverlet
//   actual coverage                          <- <results>/<Project>.coverage.cobertura.*.xml
//                                               (the prefix comes from Directory.Build.targets)
//
// It applies the threshold the way coverlet does: thresholdStat total = all lines/branches together,
// minimum = the worst module, average = the mean of the modules. thresholdType "method" isn't in a
// Cobertura report's totals, so it's listed as not checked here (coverlet still enforces it).
//
// --summary appends a Markdown table; on Azure DevOps (TF_BUILD) it is attached to the run summary and
// each failure is logged as a pipeline error. Exit code 1 if any project is under its threshold or
// has no report.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i + 1 < args.Length; i += 2)
{
    options[args[i].TrimStart('-')] = args[i + 1];
}

if (!options.TryGetValue("results", out var resultsDir))
{
    Console.Error.WriteLine("usage: coverage-report.cs --results <dir> [--tests <dir>] [--summary <file.md>]");
    return 2;
}

var testsDir = options.GetValueOrDefault("tests") ?? "tests";
var onAzureDevOps = Environment.GetEnvironmentVariable("TF_BUILD") is not null;

var rows = new List<string>();
var failures = new List<string>();

foreach (var config in Directory.GetDirectories(testsDir).Select(d => Path.Combine(d, "testconfig.json")).Where(File.Exists).Order(StringComparer.Ordinal))
{
    var project = Path.GetFileName(Path.GetDirectoryName(config)!);
    var coverlet = JsonNode.Parse(
        File.ReadAllText(config),
        documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?["platformOptions"]?["Coverlet"];

    if (coverlet is null)
    {
        rows.Add($"| {project} | — | — | — | — | ⚪ no Coverlet section in testconfig.json |");
        continue;
    }

    var threshold = coverlet["threshold"]?.GetValue<double>();
    var types = (coverlet["thresholdType"]?.GetValue<string>() ?? "line").ToLowerInvariant().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    var stat = (coverlet["thresholdStat"]?.GetValue<string>() ?? "minimum").ToLowerInvariant();

    var report = Directory.Exists(resultsDir)
        ? Directory.GetFiles(resultsDir, $"{project}.coverage.cobertura*.xml", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        : null;
    if (report is null)
    {
        failures.Add($"{project}: no {project}.coverage.cobertura*.xml in {resultsDir} (was the run started with --coverlet?)");
        rows.Add($"| {project} | — | — | {Describe(threshold, types, stat)} | — | ❌ no report |");
        continue;
    }

    var line = Rate(report, "line", stat);
    var branch = Rate(report, "branch", stat);

    var problems = new List<string>();
    foreach (var type in types)
    {
        var actual = type switch { "line" => line, "branch" => branch, _ => (double?)null };
        if (threshold is { } t && actual is { } a && a < t)
        {
            problems.Add($"{type} {Pct(a)} < {Pct(t)}");
        }
    }

    var notChecked = types.Where(t => t is not ("line" or "branch")).ToList();
    var result = problems.Count > 0
        ? "❌ " + string.Join(", ", problems)
        : threshold is null ? "⚪ no threshold" : "✅ pass" + (notChecked.Count > 0 ? $" ({string.Join(",", notChecked)} not checked here)" : "");

    if (problems.Count > 0)
    {
        failures.Add($"{project}: {string.Join(", ", problems)} ({stat})");
    }

    rows.Add($"| {project} | {Pct(line)} | {Pct(branch)} | {Describe(threshold, types, stat)} | {Path.GetFileName(report)} | {result} |");
}

var table = new StringBuilder()
    .AppendLine("## Code coverage per test project (coverlet.MTP)")
    .AppendLine()
    .AppendLine("| Project | Line | Branch | Threshold | Report | Result |")
    .AppendLine("|---|---:|---:|---|---|---|");
rows.ForEach(r => table.AppendLine(r));
Console.WriteLine(table);

if (options.TryGetValue("summary", out var summary))
{
    File.AppendAllText(summary, table.ToString());
    if (onAzureDevOps)
    {
        Console.WriteLine($"##vso[task.uploadsummary]{Path.GetFullPath(summary)}");
    }
}

foreach (var failure in failures)
{
    Console.WriteLine(onAzureDevOps ? $"##vso[task.logissue type=error]{failure}" : $"FAIL {failure}");
}

return failures.Count == 0 ? 0 : 1;

// Coverage % for "line" or "branch", using the statistic coverlet uses for its threshold.
static double Rate(string report, string kind, string stat)
{
    var root = XDocument.Load(report).Root!;
    if (stat == "total")
    {
        return double.Parse(root.Attribute($"{kind}-rate")!.Value, CultureInfo.InvariantCulture) * 100;
    }

    var perModule = root.Descendants("package")
        .Select(p => double.Parse(p.Attribute($"{kind}-rate")!.Value, CultureInfo.InvariantCulture) * 100)
        .DefaultIfEmpty(100)
        .ToList();
    return stat == "average" ? perModule.Average() : perModule.Min();
}

static string Describe(double? threshold, string[] types, string stat) =>
    threshold is { } t ? $"{Pct(t)} {string.Join("+", types)} ({stat})" : "—";

static string Pct(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
