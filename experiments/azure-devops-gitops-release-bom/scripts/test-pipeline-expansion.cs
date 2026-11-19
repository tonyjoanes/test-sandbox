#!/usr/bin/env dotnet
// Expands the Azure DevOps pipelines the way Azure DevOps does at queue time, and checks the
// job graph the sync template produces. Run with:
//
//   dotnet run test-pipeline-expansion.cs
//
// The expansion is done by Runner.Server (https://github.com/ChristopherHX/runner.server), an
// unofficial, open-source Azure Pipelines emulator, installed into scripts/.tools on first run.
// The same approach, and the same caveats, as ../../azure-devops-versioned-release-manifest:
//   - Runner.Server isn't Microsoft's implementation. Passing is strong evidence, not proof.
//   - After expanding, Runner.Server tries to download task definitions from pkgs.dev.azure.com.
//     Where that host is blocked, every task reports "Object reference not set to an instance
//     of an object". That happens after expansion, so those errors are ignored. Any other
//     template error fails the test.

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const string RunnerServerVersion = "3.14.0.8";
const string SyncTemplate = "pipelines/templates/sync-environment.yml";

var dir = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var experiment = Path.GetFullPath(Path.Combine(dir, ".."));
var repoRoot = experiment;
while (!Directory.Exists(Path.Combine(repoRoot, ".git"))) repoRoot = Path.GetDirectoryName(repoRoot) ?? throw new Exception("Not inside a git repository");
var gharun = InstallRunnerServer();
var failures = 0;

void Check(string name, IEnumerable<string?> problems)
{
    var list = problems.OfType<string>().ToList();
    Console.WriteLine(list.Count == 0 ? $"✓ {name}" : $"✗ {name}\n    {string.Join("\n    ", list)}");
    if (list.Count > 0) failures++;
}

string? Same(object? actual, object expected, string what) =>
    Equals(actual?.ToString(), expected.ToString()) ? null : $"{what}: got \"{actual}\", expected \"{expected}\"";

// ── The live sync ───────────────────────────────────────────────────────────────────────────
{
    var (snapshot, errors, expanded) = Expand("pipelines/sync-live.yml");
    Check("sync-live.yml expands with no template errors", [snapshot is null ? "no expanded pipeline found" : null, .. errors]);
    if (snapshot is not null)
    {
        var stages = Stages(snapshot);
        Check("live: plan → change_request → sync → record", [Same(string.Join(" → ", stages.Keys), "plan → change_request → sync → record", "stages")]);
        Check("live: sync waits for the change request, but only if one was needed",
        [
            Same(string.Join(",", Strings(stages["sync"]["dependsOn"])), "plan,change_request", "sync dependsOn"),
            Contains(stages["sync"]["condition"], "in(dependencies.change_request.result, 'Succeeded', 'Skipped')"),
            Contains(stages["change_request"]["condition"], "plan.plan.changeRequest"),
        ]);

        var jobs = Jobs(stages["sync"]);
        Check("live: one approval stage holds every job, in order",
        [
            Same(string.Join(" ", jobs.Keys), "pre_sync pause_1 wave_1 pause_2 wave_2 pause_3 wave_3 pause_4 wave_4 pause_5 wave_5 post_sync", "jobs"),
            .. jobs.Where(j => j.Key.StartsWith("wave_") || j.Key.EndsWith("_sync")).Select(j => Same(j.Value["environment"], "live", $"{j.Key} environment")),
        ]);

        var waveChecks = new List<string?>();
        for (var w = 1; w <= 5; w++)
        {
            var after = w == 1 ? "pre_sync" : $"wave_{w - 1}";
            var pause = jobs[$"pause_{w}"];
            var wave = jobs[$"wave_{w}"];
            waveChecks.Add(Same(pause["pool"], "server", $"pause_{w} pool"));
            waveChecks.Add(Same(string.Join(",", Strings(pause["dependsOn"])), after, $"pause_{w} dependsOn"));
            waveChecks.Add(Contains(pause["condition"], $"eq(dependencies.{after}.result, 'Succeeded')"));
            waveChecks.Add(Contains(pause["condition"], $"plan.pause{w}'], 'true'"));
            waveChecks.Add(Same(string.Join(",", Strings(wave["dependsOn"])), $"{after},pause_{w}", $"wave_{w} dependsOn"));
            waveChecks.Add(Contains(wave["condition"], $"eq(dependencies.{after}.result, 'Succeeded')"));
            waveChecks.Add(Contains(wave["condition"], $"in(dependencies.pause_{w}.result, 'Succeeded', 'Skipped')"));
            waveChecks.Add(Contains(wave["condition"], $"plan.wave{w}'], 'true'"));
        }
        Check("live: each wave follows the last, and its pause sits in between", waveChecks);

        Check("live: post-sync waits for everything and stops on any failure",
        [
            Same(Strings(jobs["post_sync"]["dependsOn"]).Count(), 11, "post_sync dependsOn count"),
            Contains(jobs["post_sync"]["condition"], "not(failed())"),
        ]);

        // The nested templates: one `relctl apply` per sync job, each with its own part.
        var parts = Regex.Matches(expanded, @"relctl apply --plan [^\n]*?plan\.json\\"" (--[a-z-]+(?: \d)?)").Select(m => m.Groups[1].Value).Distinct().Order().ToList();
        Check("live: each sync job applies its own part of the plan",
            [Same(string.Join(", ", parts), "--post-sync, --pre-sync, --wave 1, --wave 2, --wave 3, --wave 4, --wave 5", "relctl apply parts")]);
    }
}

// ── Dev: no change request stage at all ─────────────────────────────────────────────────────
{
    var (snapshot, errors, _) = Expand("pipelines/sync-dev.yml");
    Check("sync-dev.yml expands with no template errors", [snapshot is null ? "no expanded pipeline found" : null, .. errors]);
    if (snapshot is not null)
    {
        var stages = Stages(snapshot);
        Check("dev: no change_request stage, and sync doesn't wait for one",
        [
            Same(string.Join(" → ", stages.Keys), "plan → sync → record", "stages"),
            Same(string.Join(",", Strings(stages["sync"]["dependsOn"])), "plan", "sync dependsOn"),
            stages["sync"]["condition"]!.ToString().Contains("change_request") ? "sync condition mentions change_request" : null,
        ]);
    }
}

// ── The other pipelines only need to expand ─────────────────────────────────────────────────
foreach (var file in new[] { "pipelines/release-validation.yml", "pipelines/drift-nightly.yml", "pipelines/sync-uat.yml", "pipelines/sync-staging.yml" })
{
    var (_, errors, expanded) = Expand(file, template: null);
    Check($"{Path.GetFileName(file)} expands with no template errors", [expanded.Length == 0 ? "no output" : null, .. errors]);
}

// ── The harness itself: a broken template must fail ────────────────────────────────────────
{
    var broken = Path.Combine(experiment, "pipelines", "broken-for-test.yml");
    File.WriteAllText(broken, "extends:\n  template: templates/sync-environment.yml\n  parameters:\n    environment: live\n    serviceConection: typo\n");
    try
    {
        var (_, errors, _) = Expand("pipelines/broken-for-test.yml");
        Check("a pipeline with a misspelled parameter fails to expand", [errors.Count == 0 ? "expected a template error, got none" : null]);
    }
    finally { File.Delete(broken); }
}

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

// ── helpers ─────────────────────────────────────────────────────────────────────────────────

static Dictionary<string, JsonObject> Stages(JsonObject p) =>
    p["stages"]!.AsArray().Select(s => s!.AsObject()).ToDictionary(s => s["stage"]!.ToString());

static Dictionary<string, JsonObject> Jobs(JsonObject stage) =>
    stage["jobs"]!.AsArray().Select(j => j!.AsObject()).ToDictionary(j => (j["job"] ?? j["deployment"])!.ToString());

static IEnumerable<string> Strings(JsonNode? node) =>
    node is JsonArray a ? a.Select(x => x!.ToString()) : node is null ? [] : [node.ToString()];

static string? Contains(JsonNode? condition, string expected) =>
    condition?.ToString().Contains(expected) == true ? null : $"condition doesn't contain {expected}: {condition}";

string InstallRunnerServer()
{
    var toolDir = Path.Combine(dir, ".tools");
    var exe = Path.Combine(toolDir, OperatingSystem.IsWindows() ? "gharun.exe" : "gharun");
    if (!File.Exists(exe) || !Run("dotnet", ["tool", "list", "--tool-path", toolDir]).Output.Contains(RunnerServerVersion))
    {
        Console.WriteLine($"Installing Runner.Server {RunnerServerVersion} into scripts/.tools...");
        if (File.Exists(exe)) Run("dotnet", ["tool", "uninstall", "--tool-path", toolDir, "io.github.christopherhx.gharun"]);
        var install = Run("dotnet", ["tool", "install", "--tool-path", toolDir, "io.github.christopherhx.gharun", "--version", RunnerServerVersion]);
        if (install.ExitCode != 0) throw new Exception($"Couldn't install Runner.Server:\n{install.Output}");
    }
    return exe;
}

// Returns the pipeline as it looks once `template` has been evaluated (nested step templates are
// expanded afterwards, and only show in the raw output), the real template errors, and the raw output.
(JsonObject? Snapshot, List<string> Errors, string Output) Expand(string pipeline, string? template = SyncTemplate)
{
    var relative = Path.GetRelativePath(repoRoot, Path.Combine(experiment, pipeline));
    var output = Run(gharun, ["--event", "azpipelines", "-W", relative, "--list"]).Output;
    var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    JsonObject? snapshot = null;
    if (template is not null)
    {
        var marker = $"Finished evaluating template '(self)/{Path.GetRelativePath(repoRoot, Path.Combine(experiment, template)).Replace('\\', '/')}'";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].EndsWith(marker) || i + 1 >= lines.Count || lines[i + 1] != "| {") continue;
            var json = new List<string>();
            for (var j = i + 1; j < lines.Count && lines[j].StartsWith("| "); j++)
            {
                json.Add(lines[j][2..]);
                if (lines[j] == "| }") break;
            }
            snapshot = JsonNode.Parse(string.Join('\n', json))!.AsObject();
        }
    }

    var errors = lines.Where(l => l.Contains("The template is not valid."))
        .SelectMany(l => Regex.Split(l, @",(?=FileId: )"))
        .Where(e => !e.Contains("Object reference not set to an instance of an object"))
        .Select(e => e.Trim().TrimStart('|').Trim())
        .Where(e => !Regex.IsMatch(e, @"^The template is not valid\. FileId: \d+ \(Line: \d+, Col: \d+\): Found Errors inside Template Reference: The template is not valid\. FileId: \d+ \(Line: \d+, Col: \d+\):$"))
        .Distinct()
        .ToList();
    return (snapshot, errors, output);
}

(int ExitCode, string Output) Run(string file, string[] args)
{
    var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = repoRoot };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    return (p.ExitCode, stdout.Result + stderr.Result);
}
