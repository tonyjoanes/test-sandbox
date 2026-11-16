#!/usr/bin/env dotnet
// End-to-end proof of the VSTest -> MTP flip, run against a throwaway copy of this experiment.
// A .NET 10 file-based app: `dotnet run scripts/test-migration.cs` (from the experiment folder).
//
//   1. VSTest   `dotnet test --collect "XPlat Code Coverage"` (coverlet.collector), as before migrating
//   2. Config   each testconfig.json still matches its coverage.runsettings (convert --check)
//   3. Flip     add "test": { "runner": "Microsoft.Testing.Platform" } to global.json, nothing else
//   4. MTP      `dotnet test --solution --coverlet` (coverlet.MTP): passes, and every project's line
//               and branch coverage is IDENTICAL to step 1, so existing thresholds still hold
//   5. Gate     raise one project's threshold above its coverage: the MTP run fails (exit code 14)
//               and scripts/coverage-report.cs names the project and the number
//   6. Rollback remove the global.json line again: VSTest runs and passes as in step 1
//
// Exit code 0 only if every check holds.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

var experiment = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(experiment, "Orders.slnx")))
{
    Console.Error.WriteLine("Run from the experiments/azure-devops-vstest-to-mtp-migration folder.");
    return 2;
}

var keep = args.Contains("--keep");
var work = Path.Combine(Path.GetTempPath(), "vstest-to-mtp-" + Guid.NewGuid().ToString("N")[..8]);
CopyTree(experiment, work);
Console.WriteLine($"Working copy: {work}\n");

var failures = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {what}");
    if (!ok)
    {
        failures.Add(what);
    }
}

try
{
    // 1. VSTest, as the project is today.
    Console.WriteLine("1. VSTest + coverlet.collector");
    var vstestResults = Path.Combine(work, "r-vstest");
    var vstest = Run("dotnet", $"test Orders.slnx --collect \"XPlat Code Coverage\" --results-directory {vstestResults}");
    Check(vstest.ExitCode == 0, "VSTest run passes");
    var before = CoverageByModule(vstestResults);
    foreach (var (module, rate) in before)
    {
        Console.WriteLine($"       {module}: line {rate.Line:0.00}%, branch {rate.Branch:0.00}%");
    }

    Check(before.Count == 2, "one coverage report per test project (each scoped by its runsettings)");

    // 2. Converted config is in sync with the runsettings.
    Console.WriteLine("2. testconfig.json matches coverage.runsettings");
    foreach (var project in new[] { "Orders.Domain.Tests", "Orders.Api.Tests" })
    {
        var convert = Run("dotnet", $"run scripts/convert-coverage-config.cs -- tests/{project}/coverage.runsettings --check");
        Check(convert.ExitCode == 0, $"{project}: convert --check");
    }

    // 3. The flip: one line in global.json.
    Console.WriteLine("3. Flip global.json to Microsoft.Testing.Platform");
    SetRunner(work, mtp: true);

    // 4. MTP run, same numbers.
    Console.WriteLine("4. MTP + coverlet.MTP");
    var mtpResults = Path.Combine(work, "r-mtp");
    var mtp = Run("dotnet", $"test --solution Orders.slnx --coverlet --report-xunit-trx --results-directory {mtpResults}");
    Check(mtp.ExitCode == 0, "MTP run passes, thresholds from testconfig.json met");
    var after = CoverageByModule(mtpResults);
    foreach (var (module, rate) in after)
    {
        var same = before.TryGetValue(module, out var b) && b == rate;
        Check(same, $"{module}: line {rate.Line:0.00}%, branch {rate.Branch:0.00}% (VSTest: {(b is null ? "missing" : $"{b.Line:0.00}% / {b.Branch:0.00}%")})");
    }

    Check(Directory.GetFiles(mtpResults, "*.trx").Length == 2, "a TRX file per test project");
    Check(Run("dotnet", $"run scripts/coverage-report.cs -- --results {mtpResults}").ExitCode == 0, "coverage-report: every project at or above its threshold");

    // 5. A threshold failure is caught and explained.
    Console.WriteLine("5. Domain threshold raised to 99%");
    var domainConfig = Path.Combine(work, "tests", "Orders.Domain.Tests", "testconfig.json");
    var json = JsonNode.Parse(File.ReadAllText(domainConfig))!;
    json["platformOptions"]!["Coverlet"]!["threshold"] = 99;
    File.WriteAllText(domainConfig, json.ToJsonString());
    var gateResults = Path.Combine(work, "r-gate");
    var gate = Run("dotnet", $"test --project tests/Orders.Domain.Tests --coverlet --results-directory {gateResults}");
    Check(gate.ExitCode == 14, $"MTP run fails with exit code 14 (got {gate.ExitCode})");
    var report = Run("dotnet", $"run scripts/coverage-report.cs -- --results {gateResults}");
    Check(report.ExitCode == 1 && report.Output.Contains("Orders.Domain.Tests: line", StringComparison.Ordinal), "coverage-report names the project and the line coverage");

    // 6. Rolling back is the same one line.
    Console.WriteLine("6. Roll back global.json to VSTest");
    SetRunner(work, mtp: false);
    var rollbackResults = Path.Combine(work, "r-rollback");
    var rollback = Run("dotnet", $"test Orders.slnx --collect \"XPlat Code Coverage\" --results-directory {rollbackResults}");
    Check(rollback.ExitCode == 0, "VSTest run passes again");
    Check(CoverageByModule(rollbackResults).SequenceEqual(before), "same coverage as step 1");
}
finally
{
    if (keep)
    {
        Console.WriteLine($"Kept {work}");
    }
    else
    {
        Directory.Delete(work, recursive: true);
    }
}

Console.WriteLine(failures.Count == 0 ? "\nAll checks passed." : $"\n{failures.Count} check(s) failed.");
return failures.Count == 0 ? 0 : 1;

(int ExitCode, string Output) Run(string file, string arguments)
{
    var psi = new ProcessStartInfo(file, arguments)
    {
        WorkingDirectory = work,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    psi.Environment["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1";
    psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    using var process = Process.Start(psi)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    var output = stdout.Result + stderr.Result;
    if (Environment.GetEnvironmentVariable("VERBOSE") == "1")
    {
        Console.WriteLine(output);
    }

    return (process.ExitCode, output);
}

// Module name -> (line %, branch %), from every Cobertura report under a results folder.
// Both runners write one report per test project, each scoped to one module.
static SortedDictionary<string, Rate> CoverageByModule(string resultsDir)
{
    var result = new SortedDictionary<string, Rate>(StringComparer.Ordinal);
    if (!Directory.Exists(resultsDir))
    {
        return result;
    }

    foreach (var file in Directory.GetFiles(resultsDir, "*cobertura*.xml", SearchOption.AllDirectories))
    {
        foreach (var package in XDocument.Load(file).Descendants("package"))
        {
            result[(string)package.Attribute("name")!] = new Rate(Percent(package, "line-rate"), Percent(package, "branch-rate"));
        }
    }

    return result;
}

static double Percent(XElement element, string attribute) =>
    Math.Round(double.Parse((string)element.Attribute(attribute)!, CultureInfo.InvariantCulture) * 100, 2);

static void SetRunner(string root, bool mtp)
{
    var path = Path.Combine(root, "global.json");
    var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    json.Remove("test");
    if (mtp)
    {
        json["test"] = new JsonObject { ["runner"] = "Microsoft.Testing.Platform" };
    }

    File.WriteAllText(path, json.ToJsonString());
}

static void CopyTree(string from, string to)
{
    foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(from, dir);
        if (!relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "TestResults"))
        {
            Directory.CreateDirectory(Path.Combine(to, relative));
        }
    }

    foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(from, file);
        if (!relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "TestResults"))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(to, relative))!);
            File.Copy(file, Path.Combine(to, relative));
        }
    }
}

internal sealed record Rate(double Line, double Branch);
