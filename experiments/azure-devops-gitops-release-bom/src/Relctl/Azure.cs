using System.Text.Json;

namespace Relctl;

/// <summary>
/// Every Azure call relctl makes, as `az` commands. Going through the CLI (rather than the
/// Azure SDK) means the pipeline's AzureCLI@2 login is all the auth it needs, every command is
/// printable in a dry run, and tests can swap in a fake `az`.
/// </summary>
public sealed class Az(ICommandRunner runner)
{
    public CommandResult Run(bool readOnly, params string[] args)
    {
        var result = runner.Run(new Command("az", args) { ReadOnly = readOnly, Quiet = readOnly });
        return result;
    }

    private string RunOrThrow(bool readOnly, string what, params string[] args)
    {
        var result = Run(readOnly, args);
        if (!result.Ok) throw new RelctlException($"{what} failed (az exit {result.ExitCode}): {result.StdErr.Trim()}");
        return result.StdOut;
    }

    public IReadOnlyDictionary<string, string> GetTags(string resourceId)
    {
        var json = RunOrThrow(true, $"Reading tags on {resourceId}",
            "resource", "show", "--ids", resourceId, "--query", "tags", "--output", "json").Trim();
        if (json is "" or "null") return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
    }

    public void MergeTags(string resourceId, IReadOnlyDictionary<string, string> tags) =>
        RunOrThrow(false, $"Tagging {resourceId}",
            ["tag", "update", "--resource-id", resourceId, "--operation", "Merge", "--tags", .. tags.Select(t => $"{t.Key}={t.Value}")]);

    private static string[] Site(Target t) => ["--subscription", t.Subscription, "--resource-group", t.ResourceGroup, "--name", t.Name];

    public void DeployZip(ComponentType type, Target t, string zip, string? slot)
    {
        string[] slotArgs = slot is null ? [] : ["--slot", slot];
        if (type == ComponentType.FunctionApp)
            RunOrThrow(false, $"Deploying to {t.Name}",
                ["functionapp", "deployment", "source", "config-zip", .. Site(t), "--src", zip, .. slotArgs]);
        else
            RunOrThrow(false, $"Deploying to {t.Name}",
                ["webapp", "deploy", .. Site(t), "--src-path", zip, "--type", "zip", .. slotArgs]);
    }

    /// <summary>Swap a slot with production. Calling it again swaps back.</summary>
    public void Swap(ComponentType type, Target t, string slot) =>
        RunOrThrow(false, $"Swapping {t.Name}/{slot} into production",
            [type == ComponentType.FunctionApp ? "functionapp" : "webapp", "deployment", "slot", "swap", .. Site(t),
             "--slot", slot, "--target-slot", "production"]);

    public string HostName(Target t, string? slot) =>
        RunOrThrow(true, $"Looking up the host name of {t.Name}",
            ["webapp", "show", .. Site(t), .. (slot is null ? Array.Empty<string>() : ["--slot", slot]),
             "--query", "defaultHostName", "--output", "tsv"]).Trim();

    public string StaticWebEndpoint(Target t) =>
        RunOrThrow(true, $"Looking up the web endpoint of {t.Name}",
            "storage", "account", "show", "--subscription", t.Subscription, "--resource-group", t.ResourceGroup,
            "--name", t.Name, "--query", "primaryEndpoints.web", "--output", "tsv").Trim().TrimEnd('/');

    public void UploadStaticSite(Target t, string dir) =>
        RunOrThrow(false, $"Uploading the site to {t.Name}",
            "storage", "blob", "upload-batch", "--subscription", t.Subscription, "--account-name", t.Name,
            "--auth-mode", "login", "--destination", "$web", "--source", dir, "--overwrite");
}
