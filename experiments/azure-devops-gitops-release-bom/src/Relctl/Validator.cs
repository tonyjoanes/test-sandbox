namespace Relctl;

/// <summary>Package metadata lookups, cached, with errors reported once against the manifest line.</summary>
public sealed class MetadataCache(IArtifactFeed feed)
{
    private readonly Dictionary<(string, SemVer), FeedLookup> _cache = [];
    public IArtifactFeed Feed => feed;

    public FeedLookup Lookup(Component component, SemVer version)
    {
        if (!_cache.TryGetValue((component.Package, version), out var hit))
            _cache[(component.Package, version)] = hit = feed.Get(component.Package, version);
        return hit;
    }

    public PackageMetadata? Get(Component component, SemVer version) =>
        Lookup(component, version) is FeedLookup.Found f ? f.Metadata : null;
}

/// <summary>
/// Every rule a release repo must meet. Runs on every PR (as the build validation policy) and at
/// the start of every sync. The rules in three groups:
///
///   Shape        the loader: unknown keys, exact versions, names that match file names.
///   Consistency  this repo, at this commit: components exist, have a target for the
///                environment, are in the feed, and their dependency rules hold.
///   Change       this commit compared with the base branch: promotion order, no silent
///                downgrades, a new release name, and no change during a freeze.
/// </summary>
public static class Validator
{
    public static void Validate(
        ReleaseRepo head, IFileSource headFiles, ReleaseRepo? baseline, MetadataCache metadata, DateOnly today, Diagnostics d)
    {
        CheckEnvironmentsMatchPolicy(head, d);
        CheckHookScriptsExist(head, headFiles, d);
        foreach (var env in head.Environments.Values)
        {
            CheckComponents(head, env, metadata, d);
            CheckDependencies(head, env, metadata, d);
            CheckPausesAndHooksAreCurrent(env, d);
        }
        if (baseline is not null)
            foreach (var env in head.Environments.Values)
                CheckChange(head, baseline, env, today, d);
    }

    private static void CheckEnvironmentsMatchPolicy(ReleaseRepo repo, Diagnostics d)
    {
        foreach (var e in repo.Policy.Environments)
            if (!repo.Environments.ContainsKey(e.Name))
                d.Error($"environments/{e.Name}.yaml", 0, $"is missing. policy.yaml lists \"{e.Name}\", so it needs a manifest (it can list no components).");
        foreach (var env in repo.Environments.Values)
            if (repo.Policy.Environments.Count > 0 && repo.Policy.For(env.Environment) is null)
                d.Error(env.File, 1, $"\"{env.Environment}\" isn't an environment in policy.yaml. Environments: {string.Join(", ", repo.Policy.Environments.Select(e => e.Name))}.");
            else if (repo.Policy.For(env.Environment)?.PromotesFrom is not null && env.Release is null)
                d.Error(env.File, 1, $"needs a `release:` name. Every release past the first environment has one, so it can be tagged and found later.");
    }

    private static void CheckHookScriptsExist(ReleaseRepo repo, IFileSource files, Diagnostics d)
    {
        var all = repo.Catalog.Values.SelectMany(c => c.Hooks.All.Select(h => (h, c.File)))
            .Concat(repo.Environments.Values.SelectMany(e => e.Hooks.All.Select(h => (h, e.File))));
        foreach (var (hook, file) in all)
            if (hook.Script is { } script && files.Read(script) is null)
                d.Error(file, hook.Line, $"hook \"{hook.Name}\" runs {script}, which doesn't exist in the release repo.");
        foreach (var e in repo.Environments.Values)
            foreach (var h in e.Hooks.All)
                if (h.Http is { } http && !http.StartsWith("https://", StringComparison.Ordinal))
                    d.Error(e.File, h.Line, $"hook \"{h.Name}\": release-level http hooks need a full https:// URL (there's no single app to resolve {http} against).");
    }

    private static void CheckComponents(ReleaseRepo repo, EnvironmentManifest env, MetadataCache metadata, Diagnostics d)
    {
        foreach (var want in env.Components.Values)
        {
            if (!repo.Catalog.TryGetValue(want.Name, out var component))
            {
                d.Error(env.File, want.Line, $"\"{want.Name}\" isn't in the component catalog. Add components/{want.Name}.yaml first. Known: {string.Join(", ", repo.Catalog.Keys.Order())}.");
                continue;
            }
            if (!component.Targets.ContainsKey(env.Environment))
                d.Error(env.File, want.Line, $"\"{want.Name}\" has no `{env.Environment}` target in {component.File}, so relctl doesn't know where to deploy it.");

            switch (metadata.Lookup(component, want.Version))
            {
                case FeedLookup.Missing:
                    d.Error(env.File, want.Line, $"{want.Name} {want.Version} doesn't exist in the {metadata.Feed.Describe}. Only versions built from main and published can be released.");
                    break;
                case FeedLookup.Failed f:
                    d.Error(env.File, want.Line, f.Reason);
                    break;
                case FeedLookup.Found found when found.Metadata.Component != want.Name:
                    d.Error(env.File, want.Line, $"package {component.Package} {want.Version} says it is component \"{found.Metadata.Component}\", not \"{want.Name}\".");
                    break;
            }
        }
    }

    /// <summary>Each version's own rules (from inside its package) must hold in the environment as a whole.</summary>
    private static void CheckDependencies(ReleaseRepo repo, EnvironmentManifest env, MetadataCache metadata, Diagnostics d)
    {
        var graph = new Dictionary<string, List<string>>();
        foreach (var want in env.Components.Values)
        {
            if (!repo.Catalog.TryGetValue(want.Name, out var component) || metadata.Get(component, want.Version) is not { } meta) continue;
            graph[want.Name] = [];
            foreach (var req in meta.Requires)
            {
                if (!env.Components.TryGetValue(req.Component, out var other))
                {
                    d.Error(env.File, want.Line, $"{want.Name} {want.Version} needs {req.Component} >= {req.MinVersion}, but {req.Component} isn't in {env.Environment}. Why: {req.Reason}");
                    continue;
                }
                graph[want.Name].Add(req.Component);
                if (other.Version < req.MinVersion)
                    d.Error(env.File, want.Line, $"{want.Name} {want.Version} needs {req.Component} >= {req.MinVersion}, but {env.Environment} pins {req.Component} {other.Version}. Why: {req.Reason}");
            }
        }
        if (Graph.FindCycle(graph) is { } cycle)
            d.Error(env.File, 1, $"the components' requirements go round in a circle, so no order could satisfy them: {string.Join(" needs ", cycle)}.");
    }

    private static void CheckPausesAndHooksAreCurrent(EnvironmentManifest env, Diagnostics d)
    {
        foreach (var p in env.Pauses)
        {
            if (!env.Components.ContainsKey(p.Before))
                d.Error(env.File, p.Line, $"pause before \"{p.Before}\", which isn't a component in this environment.");
            if (p.Release is not null && p.Release != env.Release)
                d.Error(env.File, p.Line, $"this pause is for release {p.Release}, but this is release {env.Release ?? "(none)"}. Delete it: a stale pause would stop the next release for no reason.");
        }
        foreach (var h in env.Hooks.All)
            if (h.Release is not null && h.Release != env.Release)
                d.Error(env.File, h.Line, $"hook \"{h.Name}\" is for release {h.Release}, but this is release {env.Release ?? "(none)"}. Delete it, or remove `release:` if it should run every time.");
    }

    private static void CheckChange(ReleaseRepo head, ReleaseRepo baseline, EnvironmentManifest env, DateOnly today, Diagnostics d)
    {
        var policy = head.Policy.For(env.Environment);
        if (policy is null) return;
        baseline.Environments.TryGetValue(env.Environment, out var before);
        var beforeComponents = before?.Components ?? new Dictionary<string, DesiredComponent>();

        var changed = env.Components.Values
            .Where(c => !beforeComponents.TryGetValue(c.Name, out var b) || b.Version != c.Version)
            .ToList();

        foreach (var removed in beforeComponents.Keys.Except(env.Components.Keys))
            d.Warning(env.File, 1, $"\"{removed}\" was removed. relctl never deletes anything: it stays deployed in {env.Environment}, just unmanaged. Decommission it separately.");

        foreach (var c in env.Components.Values.Where(c => c.Rollback is not null && !changed.Contains(c)))
            d.Warning(env.File, c.Line, $"\"{c.Name}\" is still marked as a rollback from an earlier change. Tidy it back to a plain version when convenient.");

        if (changed.Count == 0) return;

        if (policy.PromotesFrom is not null && before?.Release is not null && env.Release == before.Release)
            d.Error(env.File, 1, $"versions changed but `release` is still {env.Release}. Give this release a new name: the name is how it is tagged, found and reported.");

        if (policy.Freezes.FirstOrDefault(f => today >= f.From && today <= f.To) is { } freeze)
            d.Error(env.File, 1, $"{env.Environment} is frozen from {freeze.From:yyyy-MM-dd} to {freeze.To:yyyy-MM-dd} ({freeze.Reason}). Nothing can change until then.");

        var upstream = policy.PromotesFrom is { } up && head.Environments.TryGetValue(up, out var u) ? u : null;
        foreach (var c in changed)
        {
            var previous = beforeComponents.TryGetValue(c.Name, out var b) ? b.Version : (SemVer?)null;

            if (c.Rollback is not null)
            {
                // A rollback goes back to something already proven. It skips promotion order on purpose: when live is on fire, waiting for
                // UAT isn't an option. It must actually be older, and is still checked to exist in the feed.
                if (previous is null || c.Version >= previous)
                    d.Error(env.File, c.Line, $"\"{c.Name}\" is marked as a rollback, but {c.Version} isn't older than the current {previous?.ToString() ?? "(nothing)"}. Rollbacks only go backwards.");
                continue;
            }

            if (previous is { } p && c.Version < p)
                d.Error(env.File, c.Line, $"\"{c.Name}\" goes backwards from {p} to {c.Version}. If that's a rollback, say so: `{c.Name}: {{ version: {c.Version}, rollback: \"<incident and reason>\" }}`.");

            if (upstream is not null)
            {
                upstream.Components.TryGetValue(c.Name, out var there);
                if (there is null || there.Version != c.Version)
                    d.Error(env.File, c.Line,
                        $"{c.Name} {c.Version} must be in {upstream.Environment} before it can go to {env.Environment}, but {upstream.Environment} has {(there is null ? "no " + c.Name : c.Name + " " + there.Version)}. " +
                        $"Use `relctl promote --from {upstream.Environment} --to {env.Environment}` to copy exactly what's there.");
            }
        }
    }
}

public static class Graph
{
    /// <summary>Returns one cycle (as a path that starts and ends at the same node), or null.</summary>
    public static List<string>? FindCycle(IReadOnlyDictionary<string, List<string>> edges)
    {
        var state = new Dictionary<string, int>(); // 1 = visiting, 2 = done
        var stack = new List<string>();

        List<string>? Visit(string n)
        {
            state[n] = 1;
            stack.Add(n);
            foreach (var m in edges.GetValueOrDefault(n) ?? [])
            {
                if (!edges.ContainsKey(m)) continue;
                if (state.GetValueOrDefault(m) == 1) return [.. stack.SkipWhile(x => x != m), m];
                if (state.GetValueOrDefault(m) == 0 && Visit(m) is { } found) return found;
            }
            stack.RemoveAt(stack.Count - 1);
            state[n] = 2;
            return null;
        }

        foreach (var n in edges.Keys.Order())
            if (state.GetValueOrDefault(n) == 0 && Visit(n) is { } cycle) return cycle;
        return null;
    }
}
