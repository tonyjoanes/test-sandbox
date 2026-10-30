#!/usr/bin/env dotnet
// Validates a release manifest and prints the order it will run in. This is a .NET 10
// file-based app: no project file, just
//
//   dotnet run validate-manifest.cs -- [--manifest <file>] [--rules-dir <dir>]
//                                      [--summary <out.md>] [--environments test,staging,prod]
//
// Defaults suit a local run from this folder: ../releases/release.yml checked against
// ../apis/*/release-rules.yaml. The release pipeline points --rules-dir at the downloaded
// packages instead, so the rules come from the exact versions being released.
// Exit codes: 0 valid, 1 the manifest breaks a rule, 2 a file couldn't be read.
//
// A manifest is an Azure DevOps pipeline file that `extends` pipelines/templates/release.yml.
// The pipeline only types `steps` as an object, so this file holds the actual rules:
//   1. Shape: unique job-safe ids, each step is exactly one of deploy/manual, no unknown keys.
//      Unknown keys matter: Azure DevOps would silently ignore a typo like `depends_on`, and
//      the release would run in the wrong order.
//   2. Completeness: every known API appears exactly once, with an exact x.y.z version.
//   3. Graph: every dependsOn points at a real step, and there are no cycles.
//   4. Release rules: for every API, each entry in its release-rules.yaml `deployAfter` must be
//      met. The other API's version must be at least `minVersion`, and its deploy step must
//      come BEFORE this one (directly or through other steps) in the dependsOn graph.

#:package YamlDotNet@16.3.0
#:property PublishAot=false

using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

var options = ParseArgs(args);
var scriptDir = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var manifestPath = options.GetValueOrDefault("manifest") ?? Path.GetFullPath(Path.Combine(scriptDir, "../releases/release.yml"));
var rulesDir = options.GetValueOrDefault("rules-dir") ?? Path.GetFullPath(Path.Combine(scriptDir, "../apis"));

Manifest manifest;
Dictionary<string, ApiRules> rules;
try
{
    manifest = Manifest.Load(manifestPath, options.GetValueOrDefault("environments")?.Split(','));
    rules = ApiRules.LoadAll(rulesDir);
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException or InvalidDataException)
{
    Console.Error.WriteLine($"✗ {e.Message}");
    return 2;
}

var (errors, plan) = Validator.Validate(manifest, rules);

if (errors.Count > 0)
{
    Console.Error.WriteLine($"✗ {manifestPath} breaks {errors.Count} rule(s):\n");
    foreach (var e in errors) Console.Error.WriteLine($"  - {e}\n");
    // In Azure Pipelines, also raise each error as an annotation on the run.
    if (Environment.GetEnvironmentVariable("TF_BUILD") is not null)
        foreach (var e in errors) Console.WriteLine($"##vso[task.logissue type=error]{e}");
    return 1;
}

Console.WriteLine($"✓ {manifestPath} is valid\n");
Console.WriteLine(Report.Plan(manifest, plan!));
if (options.GetValueOrDefault("summary") is { } summaryPath) File.WriteAllText(summaryPath, Report.Summary(manifest, plan!));
return 0;

static Dictionary<string, string> ParseArgs(string[] args)
{
    var known = new[] { "manifest", "rules-dir", "summary", "environments" };
    var result = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i++)
    {
        var name = args[i].StartsWith("--") ? args[i][2..] : null;
        if (name is null || !known.Contains(name) || i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"Usage: dotnet run validate-manifest.cs -- [--{string.Join(" <value>] [--", known)} <value>]");
            Environment.Exit(2);
        }
        result[name] = args[++i];
    }
    return result;
}

// ─── Loading ────────────────────────────────────────────────────────────────────────────────

static class Yaml
{
    static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    // Every scalar comes back as a string, so a version like 1.10.0 is never turned into a number.
    public static Dictionary<string, object?>? Map(object? node) =>
        node is IDictionary<object, object?> d ? d.ToDictionary(kv => kv.Key.ToString()!, kv => kv.Value) : null;

    public static Dictionary<string, object?> LoadFile(string path) =>
        Map(Deserializer.Deserialize<object?>(File.ReadAllText(path)))
        ?? throw new InvalidDataException($"{path}: expected a YAML mapping at the top level.");

    public static string? Str(this Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var v) ? v as string : null;
}

sealed record Manifest(string? Release, List<Step> Steps, bool StepsIsList, List<string> Environments)
{
    public static Manifest Load(string path, string[]? environments)
    {
        var doc = Yaml.LoadFile(path);
        var extends = Yaml.Map(doc.GetValueOrDefault("extends"));
        var parameters = Yaml.Map(extends?.GetValueOrDefault("parameters"))
            ?? throw new InvalidDataException($"{path}: expected \"extends.parameters\" (a manifest extends the release template).");

        // Environment names come from, in order: the caller, the manifest's own override, or the
        // release template's default. That way the list always matches what the pipeline runs.
        var envNames = environments?.ToList()
            ?? EnvironmentNames(parameters.GetValueOrDefault("environments"))
            ?? EnvironmentsFromTemplate(path, extends!.Str("template"));

        var rawSteps = parameters.GetValueOrDefault("steps") as List<object?>;
        var steps = (rawSteps ?? []).Select((s, i) => new Step(i, Yaml.Map(s))).ToList();
        return new Manifest(parameters.Str("release"), steps, rawSteps is not null, envNames);
    }

    static List<string>? EnvironmentNames(object? node) =>
        (node as List<object?>)?.Select(e => Yaml.Map(e)?.Str("name") ?? "").ToList();

    static List<string> EnvironmentsFromTemplate(string manifestPath, string? template)
    {
        var templatePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, template ?? ""));
        if (!File.Exists(templatePath))
            throw new FileNotFoundException($"Can't read the release template at {templatePath} to find the environment names. Pass --environments test,staging,prod");
        var envParam = (Yaml.LoadFile(templatePath).GetValueOrDefault("parameters") as List<object?> ?? [])
            .Select(Yaml.Map)
            .FirstOrDefault(p => p?.Str("name") == "environments");
        return EnvironmentNames(envParam?.GetValueOrDefault("default")) ?? [];
    }
}

sealed class Step(int index, Dictionary<string, object?>? raw)
{
    public int Index { get; } = index;
    public Dictionary<string, object?>? Raw { get; } = raw;
    public string? Id => Raw?.Str("id");
    public string? Deploy => Raw?.Str("deploy");
    public string? Version => Raw?.Str("version");
    public string? Manual => Raw?.Str("manual");
    public string? Instructions => Raw?.Str("instructions");
    public bool IsDeploy => Raw?.ContainsKey("deploy") ?? false;
    public bool IsManual => Raw?.ContainsKey("manual") ?? false;
    public List<string>? Environments => (Raw?.GetValueOrDefault("environments") as List<object?>)?.Select(e => e as string ?? "").ToList();
    public List<string> DependsOn => (Raw?.GetValueOrDefault("dependsOn") as List<object?>)?.OfType<string>().ToList() ?? [];
    public string Where => $"steps[{Index}]{(Id is null ? "" : $" ({Id})")}";
}

sealed record Dependency(string? Api, string? MinVersion, string? Reason);

// <rulesDir>/<api>/release-rules.yaml. Locally that's the apis/ folder. In the release pipeline
// it's the downloaded packages, so the rules are the ones from the exact versions being released.
sealed record ApiRules(string File, string? Api, List<Dependency> DeployAfter)
{
    public static Dictionary<string, ApiRules> LoadAll(string rulesDir)
    {
        var rules = new Dictionary<string, ApiRules>();
        foreach (var dir in Directory.GetDirectories(rulesDir).Order())
        {
            var file = Path.Combine(dir, "release-rules.yaml");
            if (!System.IO.File.Exists(file)) continue;
            var doc = Yaml.LoadFile(file);
            var deployAfter = (doc.GetValueOrDefault("deployAfter") as List<object?> ?? [])
                .Select(Yaml.Map)
                .Select(d => new Dependency(d?.Str("api"), d?.Str("minVersion"), d?.Str("reason")))
                .ToList();
            rules[Path.GetFileName(dir)] = new ApiRules(file, doc.Str("api"), deployAfter);
        }
        return rules;
    }
}

// ─── Validation ─────────────────────────────────────────────────────────────────────────────

static partial class Validator
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")] private static partial Regex StepId();   // what Azure DevOps accepts as a job name
    [GeneratedRegex(@"^\d+\.\d+\.\d+$")] private static partial Regex ExactVersion();

    static readonly string[] DeployKeys = ["id", "deploy", "version", "dependsOn"];
    static readonly string[] ManualKeys = ["id", "manual", "instructions", "environments", "dependsOn"];

    public static int CompareVersions(string a, string b)
    {
        var (x, y) = (a.Split('.').Select(int.Parse).ToArray(), b.Split('.').Select(int.Parse).ToArray());
        for (var i = 0; i < 3; i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return 0;
    }

    public static (List<string> Errors, List<List<Step>>? Plan) Validate(Manifest manifest, Dictionary<string, ApiRules> rules)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(manifest.Release)) errors.Add("`release` must be a non-empty name, e.g. 2026.11.04.");
        if (!manifest.StepsIsList || manifest.Steps.Count == 0) errors.Add("`steps` must be a non-empty list.");

        CheckRules(rules, errors);

        // 1. Step shape
        var byId = new Dictionary<string, Step>();
        var deployStepFor = new Dictionary<string, Step>();
        foreach (var step in manifest.Steps)
        {
            var where = step.Where;
            if (step.Raw is null) { errors.Add($"{where}: must be a mapping."); continue; }

            // A step with a malformed id or type is still recorded, so one mistake doesn't also
            // show up as "no step with that id" or "<api> is missing".
            if (step.Id is null || !StepId().IsMatch(step.Id))
                errors.Add($"{where}: `id` is required and may only contain letters, digits and _ (it becomes an Azure DevOps job name).");
            if (step.Id is not null && !byId.TryAdd(step.Id, step))
                errors.Add($"{where}: duplicate id \"{step.Id}\".");

            if (step.IsDeploy == step.IsManual)
            {
                errors.Add($"{where}: must have exactly one of `deploy` (an API) or `manual` (a pause for a person).");
                if (step.Deploy is not null) deployStepFor.TryAdd(step.Deploy, step);
                continue;
            }

            var allowed = step.IsDeploy ? DeployKeys : ManualKeys;
            foreach (var key in step.Raw.Keys.Where(k => !allowed.Contains(k)))
                errors.Add($"{where}: unknown key `{key}`. {(step.IsDeploy ? "Deploy" : "Manual")} steps allow: {string.Join(", ", allowed)}.");

            if (step.Raw.TryGetValue("dependsOn", out var deps) && !(deps is List<object?> list && list.All(d => d is string)))
                errors.Add($"{where}: `dependsOn` must be a list of step ids, e.g. [deploy_customers].");

            if (step.IsDeploy)
            {
                if (step.Deploy is null || !rules.ContainsKey(step.Deploy))
                    errors.Add($"{where}: unknown API \"{step.Deploy}\". Known APIs: {string.Join(", ", rules.Keys)}.");
                else if (!deployStepFor.TryAdd(step.Deploy, step))
                    errors.Add($"{where}: {step.Deploy} is already deployed by step \"{deployStepFor[step.Deploy].Id}\". Each API appears once per release.");

                if (step.Version is null || !ExactVersion().IsMatch(step.Version))
                    errors.Add($"{where}: `version` must be an exact package version like 1.8.0, got \"{step.Version ?? step.Raw.GetValueOrDefault("version")?.ToString() ?? "nothing"}\".");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(step.Instructions))
                    errors.Add($"{where}: manual steps need `instructions`. They're what the approver sees when the release pauses.");

                if (step.Raw.ContainsKey("environments"))
                {
                    if (step.Environments is not { Count: > 0 } envs)
                        errors.Add($"{where}: `environments` must be a non-empty list. Leave it out to pause in every environment.");
                    else
                        foreach (var env in envs.Where(e => !manifest.Environments.Contains(e)))
                            errors.Add($"{where}: unknown environment \"{env}\". Environments: {string.Join(", ", manifest.Environments)}.");
                }
            }
        }

        // 2. Completeness
        foreach (var api in rules.Keys.Where(a => !deployStepFor.ContainsKey(a)))
            errors.Add($"{api} is missing. List every API in every release with its exact version, even unchanged ones (they're skipped at deploy time). That way the manifest always describes all of live.");

        // 3. Graph
        foreach (var step in byId.Values)
            foreach (var dep in step.DependsOn)
            {
                if (dep == step.Id) errors.Add($"{step.Id}: depends on itself.");
                else if (!byId.ContainsKey(dep)) errors.Add($"{step.Id}: dependsOn \"{dep}\", but there's no step with that id.");
            }
        if (FindCycle(byId) is { } cycle)
            errors.Add($"dependsOn has a cycle, so nothing in it could ever start: {string.Join(" → ", cycle)}.");

        // Stop before the rule checks if the graph itself is broken. Their messages would be noise.
        if (errors.Count > 0) return (errors, null);

        // 4. Release rules
        var ancestors = AncestorSets(byId);
        foreach (var (api, step) in deployStepFor)
            foreach (var rule in rules[api].DeployAfter)
            {
                var other = deployStepFor[rule.Api!];
                var because = rule.Reason is null ? "" : $" Why: {rule.Reason}";
                if (rule.MinVersion is not null && CompareVersions(other.Version!, rule.MinVersion) < 0)
                    errors.Add($"{api} {step.Version} needs {rule.Api} >= {rule.MinVersion}, but this release pins {rule.Api} {other.Version}.{because}");
                if (!ancestors[step.Id!].Contains(other.Id!))
                    errors.Add($"{api} must be deployed after {rule.Api}, but step \"{step.Id}\" doesn't depend on \"{other.Id}\", directly or through another step.{because} Fix: add {other.Id} to {step.Id}'s dependsOn.");
            }

        return (errors, errors.Count > 0 ? null : BuildPlan(byId));
    }

    static void CheckRules(Dictionary<string, ApiRules> rules, List<string> errors)
    {
        foreach (var (api, r) in rules)
        {
            if (r.Api != api) errors.Add($"{r.File}: `api` is \"{r.Api}\" but the folder is \"{api}\".");
            foreach (var dep in r.DeployAfter)
            {
                if (dep.Api is null || !rules.ContainsKey(dep.Api)) errors.Add($"{r.File}: deployAfter refers to unknown API \"{dep.Api}\".");
                if (dep.MinVersion is not null && !ExactVersion().IsMatch(dep.MinVersion)) errors.Add($"{r.File}: minVersion for {dep.Api} must look like 1.2.0.");
                if (string.IsNullOrWhiteSpace(dep.Reason)) errors.Add($"{r.File}: deployAfter {dep.Api} needs a `reason`. It's shown to whoever has to fix a failing release.");
            }
        }
    }

    static List<string>? FindCycle(Dictionary<string, Step> byId)
    {
        var state = new Dictionary<string, bool>();   // false = visiting, true = done
        var path = new List<string>();

        List<string>? Visit(string id)
        {
            if (!byId.ContainsKey(id)) return null;
            if (state.TryGetValue(id, out var done)) return done ? null : [.. path.Skip(path.IndexOf(id)), id];
            state[id] = false;
            path.Add(id);
            foreach (var dep in byId[id].DependsOn)
                if (Visit(dep) is { } found) return found;
            path.RemoveAt(path.Count - 1);
            state[id] = true;
            return null;
        }

        foreach (var id in byId.Keys)
            if (Visit(id) is { } found) return found;
        return null;
    }

    static Dictionary<string, HashSet<string>> AncestorSets(Dictionary<string, Step> byId)
    {
        var memo = new Dictionary<string, HashSet<string>>();
        HashSet<string> Of(string id)
        {
            if (memo.TryGetValue(id, out var known)) return known;
            var set = new HashSet<string>();
            foreach (var dep in byId[id].DependsOn) { set.Add(dep); set.UnionWith(Of(dep)); }
            return memo[id] = set;
        }
        foreach (var id in byId.Keys) Of(id);
        return memo;
    }

    // Groups steps into waves: a step's wave is one more than the latest wave it depends on. Every
    // step in a wave can run at the same time. This is the order the pipeline will actually run.
    static List<List<Step>> BuildPlan(Dictionary<string, Step> byId)
    {
        var wave = new Dictionary<string, int>();
        int WaveOf(string id) => wave.TryGetValue(id, out var w) ? w : wave[id] = 1 + byId[id].DependsOn.Select(WaveOf).DefaultIfEmpty(0).Max();
        return byId.Values.GroupBy(s => WaveOf(s.Id!)).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
    }
}

// ─── Output ─────────────────────────────────────────────────────────────────────────────────

static class Report
{
    static string Describe(Step step, Manifest manifest) => step.IsDeploy
        ? $"deploy {step.Deploy} {step.Version}"
        : $"PAUSE: {step.Manual} ({string.Join(", ", step.Environments ?? manifest.Environments)})";

    public static string Plan(Manifest manifest, List<List<Step>> plan)
    {
        var sb = new StringBuilder($"Release {manifest.Release}: {string.Join(" → ", manifest.Environments)}");
        for (var i = 0; i < plan.Count; i++)
            sb.Append($"\n  {i + 1}. {string.Join("  |  ", plan[i].Select(s => Describe(s, manifest)))}");
        return sb.ToString();
    }

    // The markdown shown on the pipeline run's "Release" tab.
    public static string Summary(Manifest manifest, List<List<Step>> plan)
    {
        var steps = plan.SelectMany(w => w).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# Release {manifest.Release}").AppendLine();
        sb.AppendLine($"Promoted through: {string.Join(" → ", manifest.Environments)}").AppendLine();
        sb.AppendLine("## Versions").AppendLine().AppendLine("| API | Version |").AppendLine("|---|---|");
        foreach (var s in steps.Where(s => s.IsDeploy)) sb.AppendLine($"| {s.Deploy} | {s.Version} |");
        sb.AppendLine().AppendLine("## Order").AppendLine().AppendLine("Steps on the same line run in parallel.").AppendLine();
        for (var i = 0; i < plan.Count; i++) sb.AppendLine($"{i + 1}. {string.Join(" · ", plan[i].Select(s => Describe(s, manifest)))}");
        var manuals = steps.Where(s => s.IsManual).ToList();
        if (manuals.Count > 0)
        {
            sb.AppendLine().AppendLine("## Manual steps");
            foreach (var s in manuals)
                sb.AppendLine().AppendLine($"### {s.Manual}").AppendLine()
                  .AppendLine($"Environments: {string.Join(", ", s.Environments ?? manifest.Environments)}").AppendLine()
                  .AppendLine(s.Instructions!.Trim());
        }
        return sb.ToString();
    }
}
