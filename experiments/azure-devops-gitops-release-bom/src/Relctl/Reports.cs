using System.Text;
using System.Text.Json.Nodes;

namespace Relctl;

public static class Reports
{
    /// <summary>The plan as Markdown: the PR summary, the pipeline's "Release plan" tab, and the change request body.</summary>
    public static string PlanMarkdown(Plan plan)
    {
        var s = new StringBuilder();
        s.AppendLine($"## {plan.Environment}: {(plan.Release is null ? "" : $"release {plan.Release}, ")}{plan.Changes.Count} change(s)");
        s.AppendLine();
        if (!plan.HasChanges)
        {
            s.AppendLine($"Nothing to do: {plan.Environment} already matches the manifest.");
            return s.ToString();
        }
        if (plan.ChangeRequest) s.AppendLine("A **change request** is required before this deploys.\n");

        s.AppendLine("| Wave | Component | Type | From | To |");
        s.AppendLine("|---|---|---|---|---|");
        foreach (var c in plan.Changes)
            s.AppendLine($"| {c.Wave} | {c.Component} | {c.Type} | {c.From ?? "_(new)_"} | **{c.To}**{(c.Rollback is null ? "" : $" ⟲ rollback: {c.Rollback}")} |");
        s.AppendLine();

        s.AppendLine("### Order");
        s.AppendLine();
        var step = 1;
        if (plan.PreSync.Count > 0) s.AppendLine($"{step++}. Pre-sync hooks: {string.Join(", ", plan.PreSync.Select(h => h.Name))}");
        for (var w = 1; w <= plan.WaveCount; w++)
        {
            foreach (var p in plan.Pauses.Where(p => p.Wave == w))
                s.AppendLine($"{step++}. ⏸ **Pause before {p.Before}**: {p.Instructions.ReplaceLineEndings(" ")}");
            s.AppendLine($"{step++}. Wave {w}: {string.Join(", ", plan.InWave(w).Select(c => $"{c.Component} {c.To}"))}");
        }
        if (plan.PostSync.Count > 0) s.AppendLine($"{step}. Post-sync hooks: {string.Join(", ", plan.PostSync.Select(h => h.Name))}");
        s.AppendLine();

        if (plan.Unchanged.Count > 0) s.AppendLine($"Unchanged: {string.Join(", ", plan.Unchanged)}\n");
        foreach (var n in plan.Notes) s.AppendLine($"> {n}\n");
        return s.ToString();
    }

    /// <summary>
    /// Output variables for the sync pipeline. Azure DevOps fixes the job graph when the run is
    /// queued, so the template always has maxWaves wave jobs; these decide which actually run.
    /// </summary>
    public static IEnumerable<(string Name, string Value)> PipelineVariables(Plan plan, int maxWaves)
    {
        yield return ("hasChanges", plan.HasChanges ? "true" : "false");
        yield return ("changeRequest", plan.ChangeRequest ? "true" : "false");
        yield return ("release", plan.Release ?? "");
        for (var w = 1; w <= maxWaves; w++)
        {
            var pause = plan.Pauses.FirstOrDefault(p => p.Wave == w);
            yield return ($"wave{w}", plan.InWave(w).Any() ? "true" : "false");
            yield return ($"pause{w}", pause is null ? "false" : "true");
            yield return ($"pause{w}Instructions", pause is null ? "" : $"Before {pause.Before}: {pause.Instructions}");
        }
    }

    public static string DiffTable(string environment, IEnumerable<(string Component, string? Desired, string? Actual)> rows)
    {
        var s = new StringBuilder();
        s.AppendLine($"{environment}:");
        foreach (var (component, desired, actual) in rows)
        {
            var mark = desired == actual ? "  " : "≠ ";
            s.AppendLine($"  {mark}{component,-22} desired {desired ?? "-",-10} actual {actual ?? "(not deployed by relctl)"}");
        }
        return s.ToString();
    }

    /// <summary>
    /// A ServiceNow change_request body, built from the plan. Field names are the standard
    /// change_request table's; map them to your instance's form (and CMDB CI names) as needed.
    /// </summary>
    public static JsonObject ChangeRequest(Plan plan, ReleaseRepo repo)
    {
        var implementation = new StringBuilder();
        for (var w = 1; w <= plan.WaveCount; w++)
        {
            foreach (var p in plan.Pauses.Where(p => p.Wave == w)) implementation.AppendLine($"PAUSE before {p.Before}: {p.Instructions}");
            implementation.AppendLine($"Wave {w}: {string.Join(", ", plan.InWave(w).Select(c => $"{c.Component} {c.From ?? "(new)"} -> {c.To}"))}");
        }
        var backout = string.Join("\n", plan.Changes.Select(c => c.From is null
            ? $"{c.Component}: newly deployed, nothing to roll back to. Disable it or deploy a fix."
            : $"{c.Component}: set back to {c.From} in environments/{plan.Environment}.yaml with `rollback:` and merge. App Service and Function Apps also swap back automatically if post-deploy checks fail."));
        var owners = plan.Changes.Select(c => repo.Catalog.GetValueOrDefault(c.Component)?.Owner).OfType<string>().Distinct();

        return new JsonObject
        {
            ["type"] = "normal",
            ["short_description"] = $"Release {plan.Release ?? "(unnamed)"} to {plan.Environment}: {string.Join(", ", plan.Changes.Select(c => $"{c.Component} {c.To}"))}",
            ["description"] = PlanMarkdown(plan),
            ["implementation_plan"] = implementation.ToString().Trim(),
            ["backout_plan"] = backout,
            ["test_plan"] = "Each component is verified on its version endpoint before and after going live, then post-sync hooks run: "
                            + string.Join(", ", plan.PostSync.Select(h => h.Name).Concat(plan.Changes.SelectMany(c => repo.Catalog.GetValueOrDefault(c.Component)?.Hooks.PostSync.Select(h => $"{c.Component}/{h.Name}") ?? []))),
            ["cmdb_ci"] = new JsonArray(plan.Changes.Select(c => (JsonNode)JsonValue.Create(c.Component)).ToArray()),
            ["u_owners"] = string.Join(", ", owners),
            ["u_release_commit"] = plan.Commit ?? "",
        };
    }

    /// <summary>
    /// The environment as a CycloneDX 1.6 bill of materials: every component at its exact
    /// version, the commit and build it came from, and what it needs. Snapshot this per release.
    /// </summary>
    public static JsonObject Bom(ReleaseRepo repo, EnvironmentManifest env, MetadataCache metadata, string? commit, DateTimeOffset now)
    {
        var components = new JsonArray();
        var dependencies = new JsonArray();
        foreach (var want in env.Components.Values.OrderBy(c => c.Name))
        {
            var component = repo.Catalog.GetValueOrDefault(want.Name);
            var meta = component is null ? null : metadata.Get(component, want.Version);
            var props = new JsonArray { Prop("relctl:type", component?.Type.Name() ?? "unknown") };
            if (component?.Owner is { } owner) props.Add(Prop("relctl:owner", owner));
            if (meta?.Commit is { } c) props.Add(Prop("relctl:build.commit", c));
            if (meta?.Build is { } b) props.Add(Prop("relctl:build.run", b));
            if (want.Rollback is { } rb) props.Add(Prop("relctl:rollback", rb));

            components.Add(new JsonObject
            {
                ["type"] = "application",
                ["bom-ref"] = $"{want.Name}@{want.Version}",
                ["name"] = want.Name,
                ["version"] = want.Version.ToString(),
                ["properties"] = props,
            });
            var needs = meta?.Requires
                .Where(r => env.Components.ContainsKey(r.Component))
                .Select(r => (JsonNode)JsonValue.Create($"{r.Component}@{env.Components[r.Component].Version}"))
                .ToArray() ?? [];
            dependencies.Add(new JsonObject { ["ref"] = $"{want.Name}@{want.Version}", ["dependsOn"] = new JsonArray(needs) });
        }

        return new JsonObject
        {
            ["bomFormat"] = "CycloneDX",
            ["specVersion"] = "1.6",
            ["serialNumber"] = $"urn:uuid:{Guid.NewGuid()}",
            ["version"] = 1,
            ["metadata"] = new JsonObject
            {
                ["timestamp"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["component"] = new JsonObject
                {
                    ["type"] = "application",
                    ["bom-ref"] = $"{env.Environment}@{env.Release ?? "unnamed"}",
                    ["name"] = env.Environment,
                    ["version"] = env.Release ?? "unnamed",
                },
                ["properties"] = new JsonArray
                {
                    Prop("relctl:environment", env.Environment),
                    Prop("relctl:release", env.Release ?? ""),
                    Prop("relctl:commit", commit ?? ""),
                },
            },
            ["components"] = components,
            ["dependencies"] = dependencies,
        };

        static JsonObject Prop(string name, string value) => new() { ["name"] = name, ["value"] = value };
    }
}
