#!/usr/bin/env dotnet
// Converts a test project's VSTest coverlet.collector settings (a .runsettings file) into the
// `platformOptions.Coverlet` section of its MTP testconfig.json, which coverlet.MTP reads.
// A .NET 10 file-based app: `dotnet run scripts/convert-coverage-config.cs -- ...`.
//
//   dotnet run scripts/convert-coverage-config.cs -- tests/Orders.Domain.Tests/coverage.runsettings \
//       [--out tests/Orders.Domain.Tests/testconfig.json] \
//       [--threshold 90] [--threshold-type line,branch] [--threshold-stat total] \
//       [--csproj tests/Orders.Domain.Tests/Orders.Domain.Tests.csproj] \
//       [--check]
//
// What it carries over:
//   * Every coverlet.collector <Configuration> element that coverlet.MTP also understands
//     (Include, Exclude, ExcludeByAttribute, ExcludeByFile, Format, SkipAutoProps, ...).
//   * Thresholds. coverlet.collector has none, so they come from wherever you kept them:
//     --threshold* options, or a coverlet.msbuild-style <Threshold>/<ThresholdType>/<ThresholdStat>
//     in the csproj given with --csproj. A threshold already in testconfig.json is kept otherwise.
//
// Why it matters: once testconfig.json has a Coverlet section, coverlet.MTP treats it as the whole
// truth and adds no defaults. A half-copied config silently changes what is measured. This copies
// everything, and refuses (exit 1) if the runsettings holds a setting it can't translate.
//
// --check writes nothing: it exits 1 if testconfig.json's Coverlet section differs from what the
// conversion would produce. Run it in CI while both files exist, so they can't drift apart.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

// coverlet.collector runsettings element -> coverlet.MTP testconfig.json key, and its JSON type.
var mapping = new Dictionary<string, (string Key, Kind Kind)>(StringComparer.Ordinal)
{
    ["Format"] = ("format", Kind.String),
    ["Include"] = ("include", Kind.String),
    ["IncludeDirectory"] = ("includeDirectory", Kind.String),
    ["Exclude"] = ("exclude", Kind.String),
    ["ExcludeByFile"] = ("excludeByFile", Kind.String),
    ["ExcludeByAttribute"] = ("excludeByAttribute", Kind.String),
    ["DoesNotReturnAttribute"] = ("doesNotReturnAttribute", Kind.String),
    ["ExcludeAssembliesWithoutSources"] = ("excludeAssembliesWithoutSources", Kind.String),
    ["UseSourceLink"] = ("useSourceLink", Kind.Bool),
    ["SingleHit"] = ("singleHit", Kind.Bool),
    ["IncludeTestAssembly"] = ("includeTestAssembly", Kind.Bool),
    ["SkipAutoProps"] = ("skipAutoProps", Kind.Bool),
    ["DeterministicReport"] = ("deterministicReport", Kind.Bool),
};

var positional = new List<string>();
var options = new Dictionary<string, string>(StringComparer.Ordinal);
var check = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--check")
    {
        check = true;
    }
    else if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
    {
        options[args[i][2..]] = args[++i];
    }
    else
    {
        positional.Add(args[i]);
    }
}

if (positional.Count != 1)
{
    Console.Error.WriteLine("usage: convert-coverage-config.cs <coverage.runsettings> [--out testconfig.json] [--threshold N] [--threshold-type line,branch] [--threshold-stat total] [--csproj path] [--check]");
    return 2;
}

var runsettingsPath = positional[0];
var outPath = options.GetValueOrDefault("out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runsettingsPath))!, "testconfig.json");

// 1. Read the coverlet.collector configuration.
var collector = XDocument.Load(runsettingsPath)
    .Descendants("DataCollector")
    .FirstOrDefault(e => string.Equals((string?)e.Attribute("friendlyName"), "XPlat code coverage", StringComparison.OrdinalIgnoreCase));
if (collector is null)
{
    Console.Error.WriteLine($"{runsettingsPath}: no <DataCollector friendlyName=\"XPlat code coverage\"> (coverlet.collector) found.");
    return 1;
}

var coverlet = new JsonObject();
var untranslatable = new List<string>();
foreach (var element in collector.Element("Configuration")?.Elements() ?? [])
{
    var name = element.Name.LocalName;
    var value = element.Value.Trim();
    if (value.Length == 0)
    {
        continue;
    }

    if (!mapping.TryGetValue(name, out var target))
    {
        untranslatable.Add(name);
        continue;
    }

    coverlet[target.Key] = target.Kind == Kind.Bool ? JsonValue.Create(bool.Parse(value)) : JsonValue.Create(value);
}

if (untranslatable.Count > 0)
{
    Console.Error.WriteLine($"{runsettingsPath}: no coverlet.MTP equivalent for: {string.Join(", ", untranslatable)}. Nothing written.");
    return 1;
}

// coverlet.collector writes Cobertura when no Format is given; keep it that way so the
// pipeline's PublishCodeCoverageResults step keeps finding the same kind of file.
coverlet["format"] ??= "cobertura";

// 2. Thresholds: command line > coverlet.msbuild properties in the csproj > what testconfig.json has now.
var existing = File.Exists(outPath) ? ParseJson(File.ReadAllText(outPath)) : new JsonObject();
var existingCoverlet = existing["platformOptions"]?["Coverlet"] as JsonObject;

var csprojProperties = options.TryGetValue("csproj", out var csproj)
    ? XDocument.Load(csproj).Descendants().Where(e => !e.HasElements).GroupBy(e => e.Name.LocalName).ToDictionary(g => g.Key, g => g.Last().Value.Trim())
    : [];

SetThreshold("threshold", "threshold", "Threshold", isNumber: true);
SetThreshold("thresholdType", "threshold-type", "ThresholdType", isNumber: false);
SetThreshold("thresholdStat", "threshold-stat", "ThresholdStat", isNumber: false);

// 3. Write (or compare) testconfig.json, keeping every other setting already in it.
var platformOptions = existing["platformOptions"] as JsonObject ?? new JsonObject();
existing["platformOptions"] = platformOptions;
var serializerOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

if (check)
{
    var current = existingCoverlet?.ToJsonString(serializerOptions) ?? "(missing)";
    var expected = coverlet.ToJsonString(serializerOptions);
    if (current == expected)
    {
        Console.WriteLine($"OK   {outPath} matches {runsettingsPath}");
        return 0;
    }

    Console.WriteLine($"DIFF {outPath} does not match {runsettingsPath}");
    Console.WriteLine($"  testconfig.json has: {current}");
    Console.WriteLine($"  runsettings gives:   {expected}");
    return 1;
}

platformOptions["Coverlet"] = coverlet;
File.WriteAllText(outPath, existing.ToJsonString(serializerOptions) + Environment.NewLine);
Console.WriteLine($"Wrote {outPath}");
Console.WriteLine(coverlet.ToJsonString(serializerOptions));
if (coverlet["threshold"] is null)
{
    Console.WriteLine("Note: no threshold set. Pass --threshold (and --threshold-type) to enforce one.");
}

return 0;

void SetThreshold(string key, string option, string msbuildProperty, bool isNumber)
{
    string? raw = options.GetValueOrDefault(option) ?? csprojProperties.GetValueOrDefault(msbuildProperty);
    if (raw is not null)
    {
        coverlet[key] = isNumber ? JsonValue.Create(int.Parse(raw)) : JsonValue.Create(raw.ToLowerInvariant());
    }
    else if (existingCoverlet?[key] is { } kept)
    {
        coverlet[key] = kept.DeepClone();
    }
}

static JsonObject ParseJson(string text) =>
    JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();

internal enum Kind
{
    String,
    Bool,
}
