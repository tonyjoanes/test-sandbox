using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relctl;

/// <summary>
/// What is actually deployed. This is what makes it GitOps rather than "run the manifest":
/// relctl compares the manifest (desired) with this (actual) and only touches the difference.
/// </summary>
public interface IStateStore
{
    /// <summary>The version relctl last deployed there, or null if it never has.</summary>
    SemVer? Get(Component component, string environment);
    void Record(Component component, string environment, SemVer version, string? release);
}

/// <summary>
/// State kept as tags on the Azure resource itself: relctl.&lt;component&gt;.version and
/// relctl.&lt;component&gt;.release. No extra database, it survives everything, and anyone can
/// see it in the portal. Tagged per component, so several static sites can share an account.
/// </summary>
public sealed class AzureTagState(Az az) : IStateStore
{
    public static string VersionTag(string component) => $"relctl.{component}.version";
    public static string ReleaseTag(string component) => $"relctl.{component}.release";

    public SemVer? Get(Component component, string environment)
    {
        var target = component.Targets[environment];
        var tags = az.GetTags(target.ResourceId(component.Type));
        return tags.TryGetValue(VersionTag(component.Name), out var v) && SemVer.TryParse(v, out var version) ? version : null;
    }

    public void Record(Component component, string environment, SemVer version, string? release)
    {
        var target = component.Targets[environment];
        var tags = new Dictionary<string, string> { [VersionTag(component.Name)] = version.ToString() };
        if (release is not null) tags[ReleaseTag(component.Name)] = release;
        az.MergeTags(target.ResourceId(component.Type), tags);
    }
}

/// <summary>State in a JSON file: { "live": { "orders-api": "1.7.3" } }. For the local demo and tests.</summary>
public sealed class FileState(string path) : IStateStore
{
    private JsonObject Load() =>
        File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [] : [];

    public SemVer? Get(Component component, string environment) =>
        Load()[environment]?[component.Name]?.GetValue<string>() is { } v && SemVer.TryParse(v, out var version) ? version : null;

    public void Record(Component component, string environment, SemVer version, string? release)
    {
        var root = Load();
        if (root[environment] is not JsonObject env) root[environment] = env = [];
        env[component.Name] = version.ToString();
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// "Assume the environment matches this manifest." Used on PRs, where the question is "what
/// would merging this change", and the answer is the difference from the target branch.
/// </summary>
public sealed class ManifestState(EnvironmentManifest? baseline) : IStateStore
{
    public SemVer? Get(Component component, string environment) =>
        baseline?.Components.TryGetValue(component.Name, out var c) == true ? c.Version : null;

    public void Record(Component component, string environment, SemVer version, string? release) =>
        throw new RelctlException("Can't record a deployment against a manifest baseline.");
}
