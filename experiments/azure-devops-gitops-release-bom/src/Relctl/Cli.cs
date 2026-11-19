using System.Text.Json;

namespace Relctl;

/// <summary>What the CLI touches in the outside world. Tests replace these with fakes.</summary>
public sealed record CliContext
{
    public ICommandRunner Runner { get; init; } = ProcessRunner.Instance;
    public HttpClient Http { get; init; } = new() { Timeout = TimeSpan.FromSeconds(15) };
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
    public ApplyOptions Apply { get; init; } = new();
}

public static class Cli
{
    public const int Ok = 0, Failed = 1, Usage = 2, Drift = 3;

    private const string Help = """
        relctl: GitOps-style releases for Azure from a manifest repo.

        Checking
          validate [--base <git-ref>] [--today yyyy-mm-dd] [--summary <file.md>]
              Lint the whole repo. With --base (the PR target), also check promotion order,
              downgrades, release names and freezes for what changed.

        Releasing
          plan   --env <env> --out <dir> [--base <git-ref>] [--pipeline]
              Compare the manifest with what's deployed and write plan.json + plan.md.
              --base: assume the environment matches that ref (for PRs, no Azure access needed).
              --pipeline: also set Azure Pipelines output variables and attach the summary.
          apply  --plan <plan.json> (--pre-sync | --wave <n> | --post-sync) [--dry-run]
          change-request --plan <plan.json> --out <file.json>

        Editing manifests (these write the file; commit and open a PR as usual)
          promote --from <env> --to <env> [--only a,b] [--release <name>]
          bump    --env <env> --component <name> --version <x.y.z>

        Reporting
          diff  [--env <env>] [--fail-on-drift]     desired vs actual, every environment by default
          bom   --env <env> [--out <file.json>]      CycloneDX bill of materials for an environment

        Everywhere
          --repo <dir>          the release repo (default: current folder)
          --local-feed <dir>    read packages from a folder instead of policy.yaml's Azure Artifacts feed
          --feed-cache <dir>    where Azure Artifacts downloads go (default: a temp folder)
          --state-file <file>   read/write deployed versions in a JSON file instead of Azure resource tags

        Exit codes: 0 ok, 1 failed or invalid, 2 bad arguments, 3 drift found (diff --fail-on-drift).
        """;

    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["validate"] = ["base", "today", "summary"],
        ["plan"] = ["env", "out", "base", "pipeline", "today"],
        ["apply"] = ["plan", "pre-sync", "wave", "post-sync", "dry-run"],
        ["change-request"] = ["plan", "out"],
        ["promote"] = ["from", "to", "only", "release"],
        ["bump"] = ["env", "component", "version"],
        ["diff"] = ["env", "fail-on-drift"],
        ["bom"] = ["env", "out"],
    };
    private static readonly string[] Common = ["repo", "local-feed", "feed-cache", "state-file"];
    private static readonly string[] Flags = ["pipeline", "pre-sync", "post-sync", "dry-run", "fail-on-drift"];

    public static int Run(string[] args, CliContext? context = null)
    {
        var ctx = context ?? new CliContext();
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Help); return args.Length == 0 ? Usage : Ok; }

        var command = args[0];
        if (!Allowed.TryGetValue(command, out var allowed))
        {
            Console.Error.WriteLine($"Unknown command \"{command}\". Run `relctl --help`.");
            return Usage;
        }

        Dictionary<string, string> o;
        try
        {
            o = ParseOptions(args[1..], [.. allowed, .. Common]);
        }
        catch (RelctlException e)
        {
            Console.Error.WriteLine(e.Message);
            return Usage;
        }

        try
        {
            return command switch
            {
                "validate" => Validate(o, ctx),
                "plan" => PlanCommand(o, ctx),
                "apply" => Apply(o, ctx),
                "change-request" => ChangeRequest(o),
                "promote" => Promote(o),
                "bump" => Bump(o),
                "diff" => Diff(o, ctx),
                "bom" => Bom(o, ctx),
                _ => Usage,
            };
        }
        catch (RelctlException e)
        {
            Log.Error(e.Message);
            return Failed;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args, string[] allowed)
    {
        var result = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new RelctlException($"Unexpected argument \"{args[i]}\".");
            var key = args[i][2..];
            if (!allowed.Contains(key)) throw new RelctlException($"Unknown option --{key}. This command takes: {string.Join(", ", allowed.Select(a => "--" + a))}.");
            if (Flags.Contains(key)) { result[key] = "true"; continue; }
            if (i + 1 >= args.Length) throw new RelctlException($"--{key} needs a value.");
            result[key] = args[++i];
        }
        return result;
    }

    private static string Need(Dictionary<string, string> o, string key) =>
        o.TryGetValue(key, out var v) ? v : throw new RelctlException($"--{key} is required.");

    private static string RepoDir(Dictionary<string, string> o) => Path.GetFullPath(o.GetValueOrDefault("repo") ?? ".");

    private static IArtifactFeed Feed(Dictionary<string, string> o, Policy policy, CliContext ctx)
    {
        if (o.TryGetValue("local-feed", out var local)) return new LocalFeed(Path.GetFullPath(local));
        if (policy.Feed is { } settings)
            return new AzureArtifactsFeed(settings, ctx.Runner, o.GetValueOrDefault("feed-cache") ?? Path.Combine(Path.GetTempPath(), "relctl-feed"));
        throw new RelctlException("No feed: set `feed:` in policy.yaml, or pass --local-feed <dir>.");
    }

    private static IStateStore State(Dictionary<string, string> o, CliContext ctx, ICommandRunner? runner = null) =>
        o.TryGetValue("state-file", out var file) ? new FileState(Path.GetFullPath(file)) : new AzureTagState(new Az(runner ?? ctx.Runner));

    private static string? Commit(string repoDir, ICommandRunner runner)
    {
        if (System.Environment.GetEnvironmentVariable("BUILD_SOURCEVERSION") is { Length: > 0 } fromPipeline) return fromPipeline;
        var r = runner.Run(new Command("git", ["rev-parse", "HEAD"]) { WorkingDirectory = repoDir, ReadOnly = true, Quiet = true });
        return r.Ok ? r.StdOut.Trim() : null;
    }

    private static DateOnly Today(Dictionary<string, string> o, CliContext ctx) =>
        o.TryGetValue("today", out var t)
            ? DateOnly.TryParseExact(t, "yyyy-MM-dd", out var d) ? d : throw new RelctlException($"--today should look like 2026-11-19, not \"{t}\".")
            : DateOnly.FromDateTime(ctx.Now().UtcDateTime);

    // ── commands ─────────────────────────────────────────────────────────────

    private static int Validate(Dictionary<string, string> o, CliContext ctx)
    {
        var dir = RepoDir(o);
        var d = new Diagnostics();
        var files = new DirectorySource(dir);
        var head = Loader.Load(files, d, dir);

        ReleaseRepo? baseline = null;
        if (o.TryGetValue("base", out var baseRef))
        {
            var baseDiagnostics = new Diagnostics();
            baseline = Loader.Load(new GitRefSource(dir, baseRef), baseDiagnostics, dir);
            if (baseDiagnostics.HasErrors)
                Log.Warn($"The base ({baseRef}) has errors of its own, so change checks against it may be incomplete. First: {baseDiagnostics.Errors.First()}");
        }

        // Only check the feed if the repo's own files are sound: otherwise the noise hides the cause.
        if (!d.HasErrors)
            Validator.Validate(head, files, baseline, new MetadataCache(Feed(o, head.Policy, ctx)), Today(o, ctx), d);

        foreach (var w in d.Warnings) Log.Warn(w.ToString());
        foreach (var e in d.Errors) Log.Error(e.ToString());

        var summary = d.HasErrors
            ? $"## ✗ Release manifests: {d.Errors.Count()} problem(s)\n\n" + string.Join("\n", d.Errors.Select(e => $"- `{e.File}:{e.Line}` {e.Message}")) + "\n"
            : $"## ✓ Release manifests are valid\n\n{string.Join("\n", head.Environments.Values.OrderBy(e => e.Environment).Select(e => $"- **{e.Environment}** {e.Release ?? ""}: {e.Components.Count} components"))}\n";
        if (d.Warnings.Any()) summary += "\n" + string.Join("\n", d.Warnings.Select(w => $"- ⚠ `{w.File}:{w.Line}` {w.Message}")) + "\n";
        if (o.TryGetValue("summary", out var summaryFile))
        {
            File.WriteAllText(summaryFile, summary);
            if (Log.InPipeline) Log.UploadSummary(Path.GetFullPath(summaryFile));
        }

        if (d.HasErrors) { Console.Error.WriteLine($"\n{d.Errors.Count()} problem(s) found."); return Failed; }
        Log.Ok($"Release repo is valid{(baseline is null ? " (no --base, so change rules weren't checked)" : $" against {o["base"]}")}.");
        return Ok;
    }

    private static int PlanCommand(Dictionary<string, string> o, CliContext ctx)
    {
        var dir = RepoDir(o);
        var envName = Need(o, "env");
        var outDir = Path.GetFullPath(Need(o, "out"));
        var repo = Loader.LoadOrThrow(dir);

        IStateStore state;
        if (o.TryGetValue("base", out var baseRef))
        {
            var baseRepo = Loader.Load(new GitRefSource(dir, baseRef), new Diagnostics(), dir);
            state = new ManifestState(baseRepo.Environments.GetValueOrDefault(envName));
        }
        else state = State(o, ctx);

        var now = o.ContainsKey("today") ? new DateTimeOffset(Today(o, ctx).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero) : ctx.Now();
        var plan = Planner.Create(repo, envName, state, new MetadataCache(Feed(o, repo.Policy, ctx)), now, Commit(dir, ctx.Runner),
            enforceFreeze: !o.ContainsKey("base")); // on a PR, `validate --base` reports freezes; plan just shows the effect

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "plan.json"), plan.ToJson());
        var markdown = Reports.PlanMarkdown(plan);
        File.WriteAllText(Path.Combine(outDir, "plan.md"), markdown);
        Console.WriteLine(markdown);

        if (o.ContainsKey("pipeline"))
        {
            foreach (var (name, value) in Reports.PipelineVariables(plan, repo.Policy.MaxWaves)) Log.SetOutput(name, value);
            Log.UploadSummary(Path.Combine(outDir, "plan.md"));
        }
        return Ok;
    }

    private static int Apply(Dictionary<string, string> o, CliContext ctx)
    {
        var dir = RepoDir(o);
        var plan = Plan.FromJson(File.ReadAllText(Need(o, "plan")));
        var repo = Loader.LoadOrThrow(dir);
        var dryRun = o.ContainsKey("dry-run");
        var runner = dryRun ? new DryRunRunner(ctx.Runner) : ctx.Runner;

        var commit = Commit(dir, ctx.Runner);
        if (plan.Commit is not null && commit is not null && plan.Commit != commit)
            throw new RelctlException($"The plan was made at commit {plan.Commit[..7]}, but the repo is at {commit[..7]}. Apply a plan from the same commit.");

        var applier = new Applier(repo, Feed(o, repo.Policy, ctx), State(o, ctx, runner), new Az(runner), runner, ctx.Http, ctx.Apply with { DryRun = dryRun });
        var parts = new[] { o.ContainsKey("pre-sync"), o.ContainsKey("wave"), o.ContainsKey("post-sync") }.Count(x => x);
        if (parts != 1) throw new RelctlException("Pass exactly one of --pre-sync, --wave <n> or --post-sync.");

        if (o.ContainsKey("pre-sync")) applier.RunReleaseHooks(plan, preSync: true);
        else if (o.ContainsKey("post-sync")) applier.RunReleaseHooks(plan, preSync: false);
        else applier.ApplyWave(plan, int.TryParse(o["wave"], out var w) && w > 0 ? w : throw new RelctlException("--wave should be 1 or more."));
        return Ok;
    }

    private static int ChangeRequest(Dictionary<string, string> o)
    {
        var repo = Loader.LoadOrThrow(RepoDir(o));
        var plan = Plan.FromJson(File.ReadAllText(Need(o, "plan")));
        var body = Reports.ChangeRequest(plan, repo);
        File.WriteAllText(Need(o, "out"), body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log.Ok($"Change request body written to {o["out"]}: {body["short_description"]}");
        return Ok;
    }

    private static int Promote(Dictionary<string, string> o)
    {
        var dir = RepoDir(o);
        var repo = Loader.LoadOrThrow(dir);
        var to = Need(o, "to");
        var path = Path.Combine(dir, "environments", $"{to}.yaml");
        var only = o.TryGetValue("only", out var list) ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
        var (text, changes, skipped) = ManifestEditor.Promote(repo, Need(o, "from"), to, File.ReadAllText(path), only, o.GetValueOrDefault("release"));

        foreach (var s in skipped) Log.Warn($"Skipped {s}.");
        if (changes.Count == 0) { Log.Ok($"{to} already has everything in {o["from"]}. Nothing to promote."); return Ok; }
        File.WriteAllText(path, text);
        foreach (var c in changes) Log.Info($"  {c.Component}: {c.From?.ToString() ?? "(new)"} → {c.To}");
        Log.Ok($"Updated environments/{to}.yaml. Commit it and open a PR: the PR is the release.");
        return Ok;
    }

    private static int Bump(Dictionary<string, string> o)
    {
        var dir = RepoDir(o);
        var repo = Loader.LoadOrThrow(dir);
        var env = Need(o, "env");
        var component = Need(o, "component");
        repo.Env(env);
        if (!repo.Catalog.ContainsKey(component)) throw new RelctlException($"\"{component}\" isn't in the component catalog.");
        var version = SemVer.TryParse(Need(o, "version"), out var v) ? v.Value : throw new RelctlException($"\"{o["version"]}\" isn't an exact x.y.z version.");
        var path = Path.Combine(dir, "environments", $"{env}.yaml");
        File.WriteAllText(path, ManifestEditor.SetVersion(File.ReadAllText(path), component, version));
        Log.Ok($"{env}: {component} → {version}");
        return Ok;
    }

    private static int Diff(Dictionary<string, string> o, CliContext ctx)
    {
        var repo = Loader.LoadOrThrow(RepoDir(o));
        var state = State(o, ctx);
        var envs = o.TryGetValue("env", out var one) ? [repo.Env(one)] : repo.Policy.Environments.Select(e => repo.Env(e.Name)).ToList();
        var drift = 0;
        foreach (var env in envs)
        {
            var rows = env.Components.Values.OrderBy(c => c.Name).Select(c =>
            {
                var component = repo.Catalog[c.Name];
                return (c.Name, (string?)c.Version.ToString(), state.Get(component, env.Environment)?.ToString());
            }).ToList();
            drift += rows.Count(r => r.Item2 != r.Item3);
            Console.WriteLine(Reports.DiffTable(env.Environment, rows));
        }
        if (drift == 0) { Log.Ok("Everything matches its manifest."); return Ok; }
        var message = $"{drift} component(s) don't match their manifest. Either a release is pending, or something was deployed outside relctl.";
        if (o.ContainsKey("fail-on-drift")) { Log.Error(message); return Drift; }
        Log.Warn(message);
        return Ok;
    }

    private static int Bom(Dictionary<string, string> o, CliContext ctx)
    {
        var dir = RepoDir(o);
        var repo = Loader.LoadOrThrow(dir);
        var env = repo.Env(Need(o, "env"));
        var bom = Reports.Bom(repo, env, new MetadataCache(Feed(o, repo.Policy, ctx)), Commit(dir, ctx.Runner), ctx.Now());
        var json = bom.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (o.TryGetValue("out", out var file)) { File.WriteAllText(file, json); Log.Ok($"Wrote {file}"); }
        else Console.WriteLine(json);
        return Ok;
    }
}
