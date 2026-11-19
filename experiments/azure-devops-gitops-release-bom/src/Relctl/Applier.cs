using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relctl;

public sealed record ApplyOptions
{
    public bool DryRun { get; init; }
    public int ProbeAttempts { get; init; } = 30;
    public TimeSpan ProbeDelay { get; init; } = TimeSpan.FromSeconds(10);
    public string WorkDir { get; init; } = Path.Combine(Path.GetTempPath(), "relctl");
}

/// <summary>
/// Carries out one part of a plan: the release-level pre-sync hooks, one wave, or the
/// release-level post-sync hooks. The pipeline calls it once per job, so each wave shows up
/// as its own job, and a pause can sit between two of them.
/// </summary>
public sealed class Applier(
    ReleaseRepo repo, IArtifactFeed feed, IStateStore state, Az az, ICommandRunner runner, HttpClient http, ApplyOptions options)
{
    public void RunReleaseHooks(Plan plan, bool preSync)
    {
        var hooks = preSync ? plan.PreSync : plan.PostSync;
        var label = preSync ? "pre-sync" : "post-sync";
        if (!plan.HasChanges) { Log.Info($"Nothing changes in {plan.Environment}, so no {label} hooks run."); return; }
        if (hooks.Count == 0) { Log.Info($"No release {label} hooks."); return; }
        var env = new Dictionary<string, string>
        {
            ["RELCTL_ENVIRONMENT"] = plan.Environment,
            ["RELCTL_RELEASE"] = plan.Release ?? "",
            ["RELCTL_CHANGES"] = string.Join(",", plan.Changes.Select(c => $"{c.Component}@{c.To}")),
        };
        foreach (var hook in hooks) RunHook(hook, label, baseUrl: null, env);
    }

    public void ApplyWave(Plan plan, int wave)
    {
        var changes = plan.InWave(wave).ToList();
        if (changes.Count == 0) { Log.Info($"Wave {wave} has nothing to deploy."); return; }
        foreach (var change in changes) Deploy(plan, change);
        Log.Ok($"Wave {wave} done: {string.Join(", ", changes.Select(c => $"{c.Component} {c.To}"))}.");
    }

    private void Deploy(Plan plan, PlannedChange change)
    {
        var component = repo.Catalog.GetValueOrDefault(change.Component)
            ?? throw new RelctlException($"{change.Component} is in the plan but not the catalog. Was the plan made from a different commit?");
        var target = component.Targets[plan.Environment];
        var to = SemVer.Parse(change.To);

        Log.Group($"{change.Component}: {change.From ?? "(new)"} → {change.To}{(change.Rollback is null ? "" : " [ROLLBACK]")}");
        try
        {
            // Re-read live state. Re-running a failed wave skips what already went out; anything
            // else that moved since the plan is a reason to stop and re-plan, not to guess.
            var current = state.Get(component, plan.Environment);
            if (current == to) { Log.Ok($"{change.Component} is already on {to}. Skipping."); return; }
            if (current?.ToString() != change.From)
                throw new RelctlException($"{change.Component} in {plan.Environment} is on {current?.ToString() ?? "(nothing)"}, but the plan expected {change.From ?? "(nothing)"}. Something else changed it since the plan was made. Re-run the release to re-plan.");

            var package = feed.Get(component.Package, to) switch
            {
                FeedLookup.Found f => f,
                FeedLookup.Missing => throw new RelctlException($"{component.Package} {to} isn't in the {feed.Describe}."),
                FeedLookup.Failed f => throw new RelctlException(f.Reason),
                _ => throw new InvalidOperationException(),
            };

            var hookEnv = new Dictionary<string, string>
            {
                ["RELCTL_ENVIRONMENT"] = plan.Environment,
                ["RELCTL_RELEASE"] = plan.Release ?? "",
                ["RELCTL_COMPONENT"] = component.Name,
                ["RELCTL_VERSION"] = to.ToString(),
                ["RELCTL_PREVIOUS_VERSION"] = change.From ?? "",
                ["RELCTL_PACKAGE_DIR"] = package.Directory,
                ["RELCTL_SUBSCRIPTION"] = target.Subscription,
                ["RELCTL_RESOURCE_GROUP"] = target.ResourceGroup,
                ["RELCTL_RESOURCE_NAME"] = target.Name,
            };

            if (component.Type == ComponentType.StaticSite) DeployStaticSite(plan, component, target, package, to, hookEnv);
            else DeployApp(component, target, package, to, change.From, hookEnv);

            if (options.DryRun) Log.Info($"[dry run] would tag {target.Name} with {AzureTagState.VersionTag(component.Name)}={to}");
            else state.Record(component, plan.Environment, to, plan.Release);
            Log.Ok($"{component.Name} {to} deployed to {plan.Environment}.");
        }
        finally
        {
            Log.EndGroup();
        }
    }

    private void DeployApp(Component c, Target t, FeedLookup.Found package, SemVer to, string? from, Dictionary<string, string> hookEnv)
    {
        var zip = Path.Combine(package.Directory, c.Type.PackageFile());
        if (!options.DryRun && !File.Exists(zip)) throw new RelctlException($"{c.Package} {to} has no {c.Type.PackageFile()}.");
        string ProdUrl() => options.DryRun ? $"https://{t.Name}.azurewebsites.net" : $"https://{az.HostName(t, null)}";

        foreach (var hook in c.Hooks.PreSync) RunHook(hook, "pre-sync", ProdUrl(), hookEnv);

        if (t.Slot is { } slot)
        {
            az.DeployZip(c.Type, t, zip, slot);
            // Nothing user-facing has changed yet. Prove the slot really runs the new build before swapping it in.
            if (!options.DryRun) WaitForVersion(c, $"https://{az.HostName(t, slot)}", to, $"the {slot} slot");
            az.Swap(c.Type, t, slot);
            try
            {
                WaitForVersion(c, ProdUrl(), to, "production");
                foreach (var hook in c.Hooks.PostSync) RunHook(hook, "post-sync", ProdUrl(), hookEnv);
            }
            catch (RelctlException e)
            {
                // The previous build is sitting in the slot, warm. Swapping again puts it back.
                Log.Error($"{e.Message} Swapping {t.Name} back.");
                az.Swap(c.Type, t, slot);
                throw new RelctlException($"{c.Name} {to} failed after going live, and was swapped back to {from ?? "the previous build"}. Production is on the old version; the slot has {to} for investigation. Cause: {e.Message}");
            }
        }
        else
        {
            az.DeployZip(c.Type, t, zip, null);
            try
            {
                WaitForVersion(c, ProdUrl(), to, "production");
                foreach (var hook in c.Hooks.PostSync) RunHook(hook, "post-sync", ProdUrl(), hookEnv);
            }
            catch (RelctlException e)
            {
                throw new RelctlException($"{c.Name} {to} failed after deploying, and {t.Name} has no slot to swap back from. Roll back with a manifest change: `{c.Name}: {{ version: {from ?? "<previous>"}, rollback: \"<reason>\" }}`. Cause: {e.Message}");
            }
        }
    }

    private void DeployStaticSite(Plan plan, Component c, Target t, FeedLookup.Found package, SemVer to, Dictionary<string, string> hookEnv)
    {
        var zip = Path.Combine(package.Directory, c.Type.PackageFile());
        var baseUrl = options.DryRun ? $"https://{t.Name}.z33.web.core.windows.net" : az.StaticWebEndpoint(t);
        foreach (var hook in c.Hooks.PreSync) RunHook(hook, "pre-sync", baseUrl, hookEnv);

        // One build for every environment: the environment's settings go into config.json now,
        // not into the bundle at build time. The app fetches /config.json when it starts.
        var config = new JsonObject();
        foreach (var (k, v) in t.Config) config[k] = v;
        config["environment"] = plan.Environment;
        config["version"] = to.ToString();

        if (options.DryRun && !File.Exists(zip))
        {
            Log.Info($"[dry run] would unpack {c.Type.PackageFile()}, write config.json {config.ToJsonString()}, and upload to {t.Name}/$web");
        }
        else
        {
            var dir = Path.Combine(options.WorkDir, $"{c.Name}-{to}-{plan.Environment}");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            ZipFile.ExtractToDirectory(zip, dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            az.UploadStaticSite(t, dir);
        }

        try
        {
            WaitForVersion(c, baseUrl, to, "the site");
            foreach (var hook in c.Hooks.PostSync) RunHook(hook, "post-sync", baseUrl, hookEnv);
        }
        catch (RelctlException e)
        {
            throw new RelctlException($"{c.Name} {to} failed after uploading. Static sites have no slot to swap back from: roll back with a manifest change. Cause: {e.Message}");
        }
    }

    private void WaitForVersion(Component c, string baseUrl, SemVer expected, string where)
    {
        if (c.VersionPath is null) { Log.Warn($"{c.Name} has no versionPath, so relctl can't confirm {where} is running {expected}."); return; }
        var url = baseUrl + c.VersionPath;
        if (options.DryRun) { Log.Info($"[dry run] would wait for {url} to report {expected}"); return; }

        string? last = null;
        for (var attempt = 1; attempt <= options.ProbeAttempts; attempt++)
        {
            last = Probe(url);
            if (last == expected.ToString()) { Log.Ok($"{where} reports {expected} ({url})"); return; }
            if (attempt < options.ProbeAttempts) Thread.Sleep(options.ProbeDelay);
        }
        throw new RelctlException($"{where} never reported {expected} at {url} (last answer: {last ?? "none"}) after {options.ProbeAttempts} tries.");
    }

    /// <summary>The version an endpoint reports: either the body as plain text, or a JSON body's "version".</summary>
    private string? Probe(string url)
    {
        try
        {
            using var response = http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return $"HTTP {(int)response.StatusCode}";
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
            if (!body.StartsWith('{')) return body.Trim('"');
            return JsonNode.Parse(body)?["version"]?.GetValue<string>();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return $"error: {e.Message}";
        }
    }

    private void RunHook(Hook hook, string kind, string? baseUrl, IReadOnlyDictionary<string, string> env)
    {
        Log.Info($"{kind} hook: {hook.Name}");
        if (hook.Script is { } script)
        {
            var result = runner.Run(new Command("bash", [Path.Combine(repo.Root, script)])
            {
                Environment = env,
                WorkingDirectory = repo.Root,
                Timeout = TimeSpan.FromSeconds(hook.TimeoutSeconds),
            });
            if (!result.Ok) throw new RelctlException($"{kind} hook \"{hook.Name}\" ({script}) failed with exit code {result.ExitCode}. {result.StdErr.Trim()}");
            return;
        }

        var url = hook.Http!.StartsWith('/') ? baseUrl + hook.Http : hook.Http;
        if (options.DryRun) { Log.Info($"[dry run] would check {url} returns 2xx"); return; }
        string? last = null;
        for (var attempt = 1; attempt <= options.ProbeAttempts; attempt++)
        {
            try
            {
                using var response = http.GetAsync(url).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode) { Log.Ok($"{hook.Name}: {url} returned {(int)response.StatusCode}"); return; }
                last = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                last = e.Message;
            }
            if (attempt < options.ProbeAttempts) Thread.Sleep(options.ProbeDelay);
        }
        throw new RelctlException($"{kind} hook \"{hook.Name}\": {url} didn't return 2xx after {options.ProbeAttempts} tries (last: {last}).");
    }
}
