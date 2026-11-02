#!/usr/bin/env dotnet
// Tests that the release template expands the way the README says. Run with:
//   dotnet run test-template-expansion.cs
//
// Azure DevOps builds the stage and job graph from ${{ }} template expressions when a run is
// queued. This test does that expansion locally with Runner.Server
// (https://github.com/ChristopherHX/runner.server), an unofficial, open-source Azure Pipelines
// emulator, then checks the expanded pipeline against the manifest:
//   - stages: validate ─► deploy_test ─► deploy_staging ─► deploy_prod ─► record
//   - in every environment, one job per manifest step, with the step's dependsOn copied across
//   - deploy steps become deployment jobs with that environment's app, resource group and
//     service connection
//   - manual steps pause with their instructions where they're listed, and are no-ops elsewhere
//   - validate downloads each package at its manifest version; deploys are skipped on PRs
//
// Caveats:
//   - Runner.Server isn't Microsoft's implementation. Passing here is strong evidence, not proof.
//     The Azure DevOps Preview API (see ../../azure-pipeline-template-validation) is the proof.
//   - This scrapes the expanded pipeline from Runner.Server's trace output, so it's pinned to
//     one Runner.Server version. Bump RunnerServerVersion deliberately and re-run.
//   - After expanding, Runner.Server tries to download task definitions from pkgs.dev.azure.com.
//     Where that host is blocked, every task step then reports "Object reference not set to an
//     instance of an object". That happens after expansion, so those errors are ignored here.
//     Any other template error fails the test.

#:package YamlDotNet@16.3.0
#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

const string RunnerServerVersion = "3.14.0.8";
const string ReleaseTemplate = "pipelines/templates/release.yml";

var dir = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var experiment = Path.GetFullPath(Path.Combine(dir, ".."));
var repoRoot = experiment;
while (!Directory.Exists(Path.Combine(repoRoot, ".git"))) repoRoot = Path.GetDirectoryName(repoRoot) ?? throw new Exception("Not inside a git repository");

var gharun = InstallRunnerServer();
var t = new Tests();

// The real manifest
{
    var manifestPath = Path.Combine(experiment, "releases/release.yml");
    var (pipeline, errors) = Expand(manifestPath, ReleaseTemplate);
    t.Check("the real manifest expands", [pipeline is null ? "no expanded pipeline found" : null, .. errors]);
    if (pipeline is not null)
    {
        var manifest = Manifest.Load(manifestPath, Path.Combine(experiment, ReleaseTemplate));
        CheckPipeline("real manifest", pipeline, manifest);

        var stages = Stages(pipeline);
        var backfill = new[] { "test", "staging", "prod" }.Select(env => Task(Job(stages[$"deploy_{env}"], "backfill_billing_profiles"))).ToArray();
        t.Check("real manifest: the backfill is a no-op in test and pauses in staging and prod",
            [Same(string.Join(", ", backfill), "Delay@1, ManualValidation@1, ManualValidation@1", "tasks")]);
        var smoke = new[] { "test", "staging", "prod" }.Select(env => Task(Job(stages[$"deploy_{env}"], "checkout_smoke_test"))).ToArray();
        t.Check("real manifest: the checkout smoke test pauses in prod only",
            [Same(string.Join(", ", smoke), "Delay@1, Delay@1, ManualValidation@1", "tasks")]);
    }
}

// A pause with no `environments` list
{
    var manifestPath = Path.Combine(dir, "test/expansion/pause-everywhere.yml");
    var (pipeline, errors) = Expand(manifestPath, ReleaseTemplate);
    t.Check("pause-everywhere.yml expands", [pipeline is null ? "no expanded pipeline found" : null, .. errors]);
    if (pipeline is not null)
    {
        CheckPipeline("pause-everywhere.yml", pipeline, Manifest.Load(manifestPath, Path.Combine(experiment, ReleaseTemplate)));
        var stages = Stages(pipeline);
        var tasks = new[] { "test", "staging", "prod" }.Select(env => Task(Job(stages[$"deploy_{env}"], "rotate_signing_key"))).ToArray();
        t.Check("pause-everywhere.yml: a pause with no environments list pauses in every environment",
            [Same(string.Join(", ", tasks), "ManualValidation@1, ManualValidation@1, ManualValidation@1", "tasks")]);
    }
}

// The harness itself: a template that can't expand must fail, not pass quietly
{
    var (pipeline, errors) = Expand(Path.Combine(dir, "test/expansion/broken.yml"), "scripts/test/expansion/broken-template.yml");
    t.Check("a template that doesn't expand is reported, with Azure DevOps' error",
    [
        pipeline is null ? null : "found an expanded pipeline for a broken template",
        errors.Any(e => e.Contains("Unexpected end of expression")) ? null : $"expected an \"Unexpected end of expression\" error, got: {string.Join(" | ", errors)}",
    ]);
}

return t.Finish();

// ─── Checks shared by every manifest ────────────────────────────────────────────────────────

void CheckPipeline(string label, JsonObject pipeline, Manifest manifest)
{
    var stages = Stages(pipeline);
    var envs = manifest.Environments;
    t.Check($"{label}: stages run validate, then each environment in order, then record",
        [Same(string.Join(" ─► ", stages.Keys), string.Join(" ─► ", ["validate", .. envs.Select(e => $"deploy_{e.Name}"), "record"]), "stages")]);

    const string NotOnPr = "ne(variables['Build.Reason'], 'PullRequest')";
    t.Check($"{label}: a PR only runs validate",
    [
        stages["validate"]["condition"] is null ? null : "validate has a condition, so it might not run on PRs",
        .. stages.Where(s => s.Key != "validate").Select(s => (s.Value["condition"]?.ToString() ?? "").Contains(NotOnPr) ? null : $"{s.Key} isn't skipped on PRs"),
    ]);

    var validateSteps = Steps(stages["validate"]["jobs"]![0]!);
    var downloads = validateSteps.Where(s => Task(s) == "UniversalPackages@0")
        .Select(s => $"{s["inputs"]!["vstsFeedPackage"]} {s["inputs"]!["vstsPackageVersion"]}");
    var published = validateSteps.Where(s => s["publish"] is not null).Select(s => s["artifact"]!.ToString());
    t.Check($"{label}: validate downloads every package at its manifest version, checks it, and publishes it",
    [
        Same(string.Join(", ", downloads), string.Join(", ", manifest.Steps.Where(s => s.Deploy is not null).Select(s => $"{s.Deploy} {s.Version}")), "downloads"),
        validateSteps.Any(s => s["bash"]?.ToString().Contains("dotnet run validate-manifest.cs") == true) ? null : "no step runs validate-manifest.cs",
        Same(string.Join(", ", published), "packages, release-scripts", "published artifacts"),
    ]);

    foreach (var env in envs)
    {
        var jobs = stages[$"deploy_{env.Name}"]["jobs"]!.AsArray().Select(j => j!.AsObject()).ToList();
        var problems = new List<string?>
        {
            Same(string.Join(", ", jobs.Select(JobName)), string.Join(", ", manifest.Steps.Select(s => s.Id)), "jobs"),
        };
        foreach (var step in manifest.Steps)
        {
            if (jobs.FirstOrDefault(j => JobName(j) == step.Id) is not { } job) continue;
            var dependsOn = job["dependsOn"]?.AsArray().Select(d => d!.ToString()) ?? [];
            problems.Add(Same(string.Join(", ", dependsOn), string.Join(", ", step.DependsOn), $"{step.Id} dependsOn"));

            if (step.Deploy is not null)
            {
                var p = Steps(job).Single()["parameters"]!;
                problems.Add(job["deployment"] is null ? $"{step.Id} isn't a deployment job" : null);
                problems.Add(Same(job["environment"]?.ToString(), env.AdoEnvironment, $"{step.Id} environment"));
                problems.Add(Same($"{p["api"]} {p["version"]}", $"{step.Deploy} {step.Version}", $"{step.Id} package"));
                problems.Add(Same(p["appName"]?.ToString(), env.AppNameFormat.Replace("{0}", step.Deploy), $"{step.Id} appName"));
                problems.Add(Same(p["resourceGroup"]?.ToString(), env.ResourceGroup, $"{step.Id} resourceGroup"));
                problems.Add(Same(p["serviceConnection"]?.ToString(), env.ServiceConnection, $"{step.Id} serviceConnection"));
            }
            else
            {
                var pauses = step.Environments is null || step.Environments.Contains(env.Name);
                var task = Steps(job).Single();
                problems.Add(Same(job["pool"]?.ToString(), "server", $"{step.Id} pool"));
                problems.Add(Same(Task(task), pauses ? "ManualValidation@1" : "Delay@1", $"{step.Id} task"));
                if (pauses)
                {
                    var instructions = task["inputs"]?["instructions"]?.ToString() ?? "";
                    problems.Add(instructions.Contains(step.Instructions!.Trim()) ? null : $"{step.Id} instructions don't include the manifest's instructions");
                    problems.Add(instructions.Contains($"paused in {env.Name}") ? null : $"{step.Id} instructions don't name the environment");
                }
            }
        }
        t.Check($"{label}: {env.Name} has one job per step, wired by dependsOn, with {env.Name}'s settings", [.. problems]);
    }

    var recordBash = string.Join("\n", Steps(stages["record"]["jobs"]![0]!).Select(s => s["bash"]?.ToString()));
    t.Check($"{label}: record tags release/{manifest.Release}",
        [recordBash.Contains($"tag=\"release/{manifest.Release}\"") && recordBash.Contains("git push origin \"$tag\"") ? null : "no tag step for this release"]);
}

static Dictionary<string, JsonObject> Stages(JsonObject pipeline) =>
    pipeline["stages"]!.AsArray().Select(s => s!.AsObject()).ToDictionary(s => s["stage"]!.ToString());

static JsonObject Job(JsonObject stage, string name) => stage["jobs"]!.AsArray().Select(j => j!.AsObject()).Single(j => JobName(j) == name);

static string JobName(JsonObject job) => (job["job"] ?? job["deployment"])!.ToString();

static List<JsonObject> Steps(JsonNode job) =>
    (job["steps"] ?? job["strategy"]!["runOnce"]!["deploy"]!["steps"])!.AsArray().Select(s => s!.AsObject()).ToList();

// A step's task name, or the task of a job's single step. Null for script steps like `bash:`.
static string? Task(JsonNode jobOrStep) =>
    jobOrStep["steps"] is not null || jobOrStep["strategy"] is not null
        ? Steps(jobOrStep).Single()["task"]?.ToString()
        : jobOrStep["task"]?.ToString();

static string? Same(string? actual, string expected, string what) => actual == expected ? null : $"{what}: got \"{actual}\", expected \"{expected}\"";

// ─── Runner.Server ──────────────────────────────────────────────────────────────────────────

string InstallRunnerServer()
{
    var toolDir = Path.Combine(dir, ".tools");
    var exe = Path.Combine(toolDir, OperatingSystem.IsWindows() ? "gharun.exe" : "gharun");
    var version = Run("dotnet", ["tool", "list", "--tool-path", toolDir], repoRoot).Output;
    if (!File.Exists(exe) || !version.Contains(RunnerServerVersion))
    {
        Console.WriteLine($"Installing Runner.Server {RunnerServerVersion} into scripts/.tools...");
        if (File.Exists(exe)) Run("dotnet", ["tool", "uninstall", "--tool-path", toolDir, "io.github.christopherhx.gharun"], repoRoot);
        var install = Run("dotnet", ["tool", "install", "--tool-path", toolDir, "io.github.christopherhx.gharun", "--version", RunnerServerVersion], repoRoot);
        if (install.ExitCode != 0) throw new Exception($"Couldn't install Runner.Server:\n{install.Output}");
    }
    return exe;
}

// Expands a manifest and returns the pipeline as it looks once <template> has been evaluated,
// plus any template errors that aren't the known task-download artefact (see the header).
(JsonObject? Pipeline, List<string> Errors) Expand(string manifestPath, string template)
{
    var relative = Path.GetRelativePath(repoRoot, manifestPath);
    var templateFromRoot = Path.GetRelativePath(repoRoot, Path.Combine(experiment, template)).Replace('\\', '/');
    var output = Run(gharun, ["--event", "azpipelines", "-W", relative, "--list"], repoRoot).Output;
    var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    // Runner.Server echoes "Finished evaluating template '<file>'" followed by the evaluated
    // result as JSON, each line prefixed with "| ". The last one for <template> is the full pipeline.
    var marker = $"Finished evaluating template '(self)/{templateFromRoot}'";
    JsonObject? pipeline = null;
    for (var i = 0; i < lines.Count; i++)
    {
        if (!lines[i].EndsWith(marker) || i + 1 >= lines.Count || lines[i + 1] != "| {") continue;
        var json = new List<string>();
        for (var j = i + 1; j < lines.Count && lines[j].StartsWith("| "); j++)
        {
            json.Add(lines[j][2..]);
            if (lines[j] == "| }") break;
        }
        pipeline = JsonNode.Parse(string.Join('\n', json))!.AsObject();
    }

    var errors = lines.Where(l => l.Contains("The template is not valid."))
        .SelectMany(l => Regex.Split(l, @",(?=FileId: )"))
        .Where(e => !e.Contains("Object reference not set to an instance of an object"))
        .Select(e => e.Trim().TrimStart('|').Trim())
        .Where(e => !Regex.IsMatch(e, @"^The template is not valid\. FileId: \d+ \(Line: \d+, Col: \d+\): Found Errors inside Template Reference: The template is not valid\. FileId: \d+ \(Line: \d+, Col: \d+\):$"))
        .Distinct()
        .ToList();
    return (pipeline, errors);
}

static (int ExitCode, string Output) Run(string file, string[] args, string workingDirectory)
{
    var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit(TimeSpan.FromMinutes(5))) { p.Kill(entireProcessTree: true); throw new TimeoutException($"{file} took over 5 minutes"); }
    return (p.ExitCode, stdout.Result + stderr.Result);
}

// ─── Manifest ───────────────────────────────────────────────────────────────────────────────

sealed record Env(string Name, string AdoEnvironment, string ServiceConnection, string ResourceGroup, string AppNameFormat);

sealed record Step(string Id, string? Deploy, string? Version, string? Instructions, List<string>? Environments, List<string> DependsOn);

sealed record Manifest(string Release, List<Step> Steps, List<Env> Environments)
{
    public static Manifest Load(string manifestPath, string templatePath)
    {
        var yaml = new DeserializerBuilder().Build();
        var parameters = (Dictionary<object, object>)((Dictionary<object, object>)yaml.Deserialize<Dictionary<object, object>>(File.ReadAllText(manifestPath))["extends"])["parameters"];
        var steps = ((List<object>)parameters["steps"]).Cast<Dictionary<object, object>>().Select(s => new Step(
            (string)s["id"],
            s.GetValueOrDefault("deploy") as string,
            s.GetValueOrDefault("version") as string,
            s.GetValueOrDefault("instructions") as string,
            (s.GetValueOrDefault("environments") as List<object>)?.Cast<string>().ToList(),
            (s.GetValueOrDefault("dependsOn") as List<object>)?.Cast<string>().ToList() ?? [])).ToList();

        // Expected environment settings come from the template's own default, like the pipeline's.
        var templateParams = (List<object>)yaml.Deserialize<Dictionary<object, object>>(File.ReadAllText(templatePath))["parameters"];
        var envDefault = (List<object>)templateParams.Cast<Dictionary<object, object>>().Single(p => (string)p["name"] == "environments")["default"];
        var envs = envDefault.Cast<Dictionary<object, object>>().Select(e => new Env(
            (string)e["name"], (string)e["adoEnvironment"], (string)e["serviceConnection"], (string)e["resourceGroup"], (string)e["appNameFormat"])).ToList();

        return new Manifest((string)parameters["release"], steps, envs);
    }
}

sealed class Tests
{
    int passed, failed;

    public void Check(string name, string?[] results)
    {
        var problems = results.OfType<string>().ToList();
        Console.WriteLine($"{(problems.Count == 0 ? "✓" : "✗")} {name}");
        foreach (var p in problems) Console.WriteLine($"    {p}");
        if (problems.Count == 0) passed++; else failed++;
    }

    public int Finish()
    {
        Console.WriteLine($"\n{passed} of {passed + failed} passed");
        return failed == 0 ? 0 : 1;
    }
}
