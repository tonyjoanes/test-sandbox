using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relctl;

public sealed record PlannedChange(string Component, string Type, string? From, string To, int Wave, string? Rollback);

public sealed record PlannedPause(int Wave, string Before, string Instructions);

/// <summary>
/// The output of `relctl plan`, and the input of `relctl apply`. Like a Terraform plan: computed
/// once, reviewed, then applied in waves. Apply refuses to act if the environment has moved
/// since the plan was made.
/// </summary>
public sealed record Plan(
    string Environment,
    string? Release,
    string? Commit,
    DateTimeOffset CreatedAt,
    int WaveCount,
    bool ChangeRequest,
    List<PlannedChange> Changes,
    List<PlannedPause> Pauses,
    List<string> Unchanged,
    List<Hook> PreSync,
    List<Hook> PostSync,
    List<string> Notes)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool HasChanges => Changes.Count > 0;
    public IEnumerable<PlannedChange> InWave(int wave) => Changes.Where(c => c.Wave == wave);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static Plan FromJson(string json) =>
        JsonSerializer.Deserialize<Plan>(json, Json) ?? throw new RelctlException("The plan file is empty.");
}

public static class Planner
{
    public static Plan Create(
        ReleaseRepo repo, string environment, IStateStore state, MetadataCache metadata,
        DateTimeOffset now, string? commit, bool enforceFreeze = true)
    {
        var env = repo.Env(environment);
        var policy = repo.Policy.For(environment) ?? throw new RelctlException($"\"{environment}\" isn't in policy.yaml.");
        var errors = new List<string>();
        var notes = new List<string>();

        // 1. Desired vs actual.
        var changes = new Dictionary<string, (Component Component, SemVer? From, DesiredComponent Want, PackageMetadata Meta)>();
        var unchanged = new List<string>();
        foreach (var want in env.Components.Values.OrderBy(c => c.Name))
        {
            if (!repo.Catalog.TryGetValue(want.Name, out var component))
            {
                errors.Add($"\"{want.Name}\" isn't in the component catalog.");
                continue;
            }
            if (!component.Targets.ContainsKey(environment))
            {
                errors.Add($"\"{want.Name}\" has no {environment} target.");
                continue;
            }
            var actual = state.Get(component, environment);
            if (actual == want.Version)
            {
                unchanged.Add($"{want.Name} {want.Version}");
                continue;
            }
            if (metadata.Get(component, want.Version) is not { } meta)
            {
                errors.Add($"{want.Name} {want.Version} couldn't be read from the {metadata.Feed.Describe}. Run `relctl validate`.");
                continue;
            }
            changes[want.Name] = (component, actual, want, meta);
        }

        // 2. Waves. Between any two waves, everything running must still have what it needs. So
        //    two changing components are ordered only when the other order would break a rule:
        //
        //    a. C's NEW version needs R >= min, and R's current version is below min:
        //       R goes first. (The normal case: ship the API before its new caller.)
        //    b. C's CURRENT version needs R >= min, and R is going below min:
        //       C goes first. (The rollback case: move the caller off the API before downgrading it.)
        //
        //    Anything not changing is already in place, and validation has checked it against the
        //    final state, so it never holds anything up.
        var needs = changes.Keys.ToDictionary(k => k, _ => new List<string>());
        foreach (var (name, c) in changes)
        {
            foreach (var req in c.Meta.Requires)
                if (changes.TryGetValue(req.Component, out var r) && (r.From is null || r.From < req.MinVersion))
                    needs[name].Add(req.Component);

            var current = c.From is { } from ? metadata.Get(c.Component, from) : null;
            foreach (var req in current?.Requires ?? [])
                if (changes.TryGetValue(req.Component, out var r) && r.Want.Version < req.MinVersion)
                    needs[req.Component].Add(name);
        }
        if (Graph.FindCycle(needs) is { } cycle)
            errors.Add($"The changing components need each other in a circle, so no order works: {string.Join(" needs ", cycle)}.");

        var wave = new Dictionary<string, int>();
        int WaveOf(string name, int depth = 0)
        {
            if (wave.TryGetValue(name, out var w)) return w;
            if (depth > changes.Count) return 1; // a cycle; already reported
            return wave[name] = 1 + needs[name].Select(n => WaveOf(n, depth + 1)).DefaultIfEmpty(0).Max();
        }
        foreach (var name in changes.Keys) WaveOf(name);
        var waveCount = wave.Values.DefaultIfEmpty(0).Max();
        if (waveCount > repo.Policy.MaxWaves)
            errors.Add($"This release needs {waveCount} waves, but the pipeline has {repo.Policy.MaxWaves} (policy.yaml maxWaves). Split the release, or raise maxWaves and add waves to the sync template.");

        // 3. Pauses hold the wave of the component they come before.
        var pauses = new List<PlannedPause>();
        foreach (var p in env.Pauses)
        {
            if (p.Release is not null && p.Release != env.Release) continue;
            if (wave.TryGetValue(p.Before, out var w)) pauses.Add(new PlannedPause(w, p.Before, p.Instructions));
            else notes.Add($"The pause before {p.Before} is skipped: {p.Before} isn't changing.");
        }
        pauses = pauses.GroupBy(p => p.Wave)
            .Select(g => new PlannedPause(g.Key, string.Join(", ", g.Select(p => p.Before)), string.Join("\n\n", g.Select(p => p.Instructions))))
            .OrderBy(p => p.Wave).ToList();

        // 4. Freezes are checked again here, at deploy time. A PR merged before the freeze must not deploy during it.
        if (enforceFreeze && changes.Count > 0 &&
            policy.Freezes.FirstOrDefault(f => DateOnly.FromDateTime(now.UtcDateTime) is var today && today >= f.From && today <= f.To) is { } freeze)
            errors.Add($"{environment} is frozen from {freeze.From:yyyy-MM-dd} to {freeze.To:yyyy-MM-dd} ({freeze.Reason}).");

        if (errors.Count > 0)
            throw new RelctlException($"Can't plan {environment}:\n  - {string.Join("\n  - ", errors)}");

        bool Current(Hook h) => h.Release is null || h.Release == env.Release;
        var hasChanges = changes.Count > 0;
        return new Plan(
            environment, env.Release, commit, now, waveCount,
            ChangeRequest: policy.ChangeRequest && hasChanges,
            Changes: changes.Values
                .Select(c => new PlannedChange(c.Component.Name, c.Component.Type.Name(), c.From?.ToString(), c.Want.Version.ToString(), wave[c.Component.Name], c.Want.Rollback))
                .OrderBy(c => c.Wave).ThenBy(c => c.Component).ToList(),
            Pauses: pauses,
            Unchanged: unchanged,
            PreSync: hasChanges ? env.Hooks.PreSync.Where(Current).ToList() : [],
            PostSync: hasChanges ? env.Hooks.PostSync.Where(Current).ToList() : [],
            Notes: notes);
    }
}
