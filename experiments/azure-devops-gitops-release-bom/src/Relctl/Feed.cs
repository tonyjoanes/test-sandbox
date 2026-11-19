namespace Relctl;

public abstract record FeedLookup
{
    public sealed record Found(PackageMetadata Metadata, string Directory) : FeedLookup;
    public sealed record Missing : FeedLookup;
    public sealed record Failed(string Reason) : FeedLookup;
}

/// <summary>Where built packages live. Each package version holds the deployable zip plus release-metadata.yaml.</summary>
public interface IArtifactFeed
{
    string Describe { get; }
    FeedLookup Get(string package, SemVer version);
}

internal static class FeedHelpers
{
    public const string MetadataFile = "release-metadata.yaml";

    public static FeedLookup ReadMetadata(string dir, string package, SemVer version)
    {
        var path = Path.Combine(dir, MetadataFile);
        if (!File.Exists(path)) return new FeedLookup.Failed($"{package} {version} has no {MetadataFile}. Was it built with the build-component template?");
        var d = new Diagnostics();
        var meta = Loader.LoadMetadata(File.ReadAllText(path), $"{package}@{version}/{MetadataFile}", d);
        if (d.HasErrors || meta is null)
            return new FeedLookup.Failed(string.Join("; ", d.Errors.Select(e => e.ToString())));
        if (meta.Version != version)
            return new FeedLookup.Failed($"{package} {version}'s metadata says it is version {meta.Version}.");
        return new FeedLookup.Found(meta, dir);
    }
}

/// <summary>A folder laid out as &lt;package&gt;/&lt;version&gt;/. Used for tests and the local demo.</summary>
public sealed class LocalFeed(string root) : IArtifactFeed
{
    public string Describe => $"local feed {root}";

    public FeedLookup Get(string package, SemVer version)
    {
        var dir = Path.Combine(root, package, version.ToString());
        return Directory.Exists(dir) ? FeedHelpers.ReadMetadata(dir, package, version) : new FeedLookup.Missing();
    }
}

/// <summary>
/// An Azure Artifacts feed of Universal Packages, fetched with `az artifacts universal download`.
/// Downloads are cached for the life of the process, and in the pipeline the cache folder is
/// published so the deploy stage uses the exact bytes the plan stage checked.
/// </summary>
public sealed class AzureArtifactsFeed(FeedSettings settings, ICommandRunner runner, string cacheDir) : IArtifactFeed
{
    public string Describe => $"Azure Artifacts feed {settings.Name}";

    public FeedLookup Get(string package, SemVer version)
    {
        var dir = Path.Combine(cacheDir, package, version.ToString());
        if (File.Exists(Path.Combine(dir, FeedHelpers.MetadataFile))) return FeedHelpers.ReadMetadata(dir, package, version);

        Directory.CreateDirectory(dir);
        List<string> args =
        [
            "artifacts", "universal", "download",
            "--organization", settings.Organization, "--feed", settings.Name,
            "--name", package, "--version", version.ToString(), "--path", dir,
        ];
        if (settings.Project is { } project) args.AddRange(["--project", project, "--scope", "project"]);

        // A download only writes to a local cache folder, so it's safe in a dry run.
        var result = runner.Run(new Command("az", args) { ReadOnly = true, Quiet = true });
        if (!result.Ok)
        {
            Directory.Delete(dir, recursive: true);
            // az doesn't give "not found" a distinct exit code, so report the message rather than guess.
            return new FeedLookup.Failed($"couldn't download {package} {version} from {settings.Name}: {result.StdErr.Trim()}");
        }
        return FeedHelpers.ReadMetadata(dir, package, version);
    }
}
