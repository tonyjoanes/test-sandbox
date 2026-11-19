namespace Relctl;

/// <summary>How a component gets deployed. Each maps to one deployer.</summary>
public enum ComponentType
{
    /// <summary>A C# web API on App Service: zip deploy to a slot, then swap.</summary>
    AppService,
    /// <summary>An Azure Function App: zip deploy to a slot, then swap.</summary>
    FunctionApp,
    /// <summary>A React (or any static) front end on a storage account's static website.</summary>
    StaticSite,
}

public static class ComponentTypes
{
    public static readonly IReadOnlyDictionary<string, ComponentType> ByName = new Dictionary<string, ComponentType>
    {
        ["app-service"] = ComponentType.AppService,
        ["function-app"] = ComponentType.FunctionApp,
        ["static-site"] = ComponentType.StaticSite,
    };

    public static string Name(this ComponentType t) => ByName.First(kv => kv.Value == t).Key;

    /// <summary>The file inside the package that gets deployed.</summary>
    public static string PackageFile(this ComponentType t) => t == ComponentType.StaticSite ? "site.zip" : "app.zip";
}

/// <summary>Where a component lives in one environment.</summary>
public sealed record Target(
    string Environment,
    string Subscription,
    string ResourceGroup,
    /// <summary>The App Service / Function App name, or the storage account name for a static site.</summary>
    string Name,
    /// <summary>Deployment slot to deploy into before swapping. Null deploys straight to production.</summary>
    string? Slot,
    /// <summary>Static sites only: written to config.json at deploy time, so one build serves every environment.</summary>
    IReadOnlyDictionary<string, string> Config,
    int Line)
{
    public string ResourceId(ComponentType type) => type == ComponentType.StaticSite
        ? $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.Storage/storageAccounts/{Name}"
        : $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.Web/sites/{Name}";
}

/// <summary>
/// A pre-sync or post-sync hook (the Argo CD names). Exactly one of Script or Http is set.
/// Release is set for one-off hooks (a backfill for one release); a hook whose Release doesn't
/// match the manifest's is stale and fails validation, so it can't quietly run again next time.
/// </summary>
public sealed record Hook(string Name, string? Script, string? Http, string? Release, int TimeoutSeconds, int Line);

public sealed record HookSet(IReadOnlyList<Hook> PreSync, IReadOnlyList<Hook> PostSync)
{
    public static readonly HookSet Empty = new([], []);
    public IEnumerable<Hook> All => PreSync.Concat(PostSync);
}

/// <summary>One entry in the component catalog (components/*.yaml): how to deploy a thing.</summary>
public sealed record Component(
    string Name,
    ComponentType Type,
    string Package,
    string? Owner,
    /// <summary>Path the app reports its version on (e.g. /version, or /config.json for a static site). Null skips verification.</summary>
    string? VersionPath,
    IReadOnlyDictionary<string, Target> Targets,
    HookSet Hooks,
    string File);

/// <summary>One line of an environment manifest: this component, at exactly this version.</summary>
public sealed record DesiredComponent(string Name, SemVer Version, string? Rollback, int Line);

/// <summary>A manual pause before a component deploys. Only applies to the release it names.</summary>
public sealed record Pause(string Before, string? Release, string Instructions, int Line);

/// <summary>environments/&lt;env&gt;.yaml: the desired state of one environment. This is the BOM.</summary>
public sealed record EnvironmentManifest(
    string Environment,
    string? Release,
    IReadOnlyDictionary<string, DesiredComponent> Components,
    IReadOnlyList<Pause> Pauses,
    HookSet Hooks,
    string File);

public sealed record Freeze(DateOnly From, DateOnly To, string Reason);

public sealed record EnvironmentPolicy(
    string Name,
    /// <summary>The environment every version must pass through first. Null for the first environment.</summary>
    string? PromotesFrom,
    bool ChangeRequest,
    IReadOnlyList<Freeze> Freezes,
    int Line);

public sealed record FeedSettings(string Organization, string? Project, string Name);

/// <summary>policy.yaml: the environment chain, freezes, and limits.</summary>
public sealed record Policy(IReadOnlyList<EnvironmentPolicy> Environments, int MaxWaves, FeedSettings? Feed, string File)
{
    public EnvironmentPolicy? For(string env) => Environments.FirstOrDefault(e => e.Name == env);
}

/// <summary>"This version of me needs at least that version of them." Ships inside the package.</summary>
public sealed record Requirement(string Component, SemVer MinVersion, string Reason);

/// <summary>release-metadata.yaml inside each published package.</summary>
public sealed record PackageMetadata(string Component, SemVer Version, string? Commit, string? Build, IReadOnlyList<Requirement> Requires);

/// <summary>Everything in the release repo, at one point in time.</summary>
public sealed record ReleaseRepo(
    string Root,
    Policy Policy,
    IReadOnlyDictionary<string, Component> Catalog,
    IReadOnlyDictionary<string, EnvironmentManifest> Environments)
{
    public EnvironmentManifest Env(string name) =>
        Environments.TryGetValue(name, out var e) ? e
        : throw new RelctlException($"No environment called \"{name}\". Environments: {string.Join(", ", Environments.Keys)}.");
}
