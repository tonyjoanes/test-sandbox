using System.IO.Compression;
using System.Net;
using System.Text.Json;

namespace Relctl.Tests;

/// <summary>
/// A throwaway copy of the example release repo and sample feed, ready to be broken one rule
/// at a time. Each package also gets a real app.zip (or a site.zip with an index.html).
/// </summary>
public sealed class TestRepo : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "relctl-tests", Guid.NewGuid().ToString("N"));
    public string Repo => Path.Combine(Root, "repo");
    public string Feed => Path.Combine(Root, "feed");

    public TestRepo()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "assets");
        CopyDir(Path.Combine(assets, "release-repo"), Repo);
        CopyDir(Path.Combine(assets, "sample-feed"), Feed);
        foreach (var versionDir in Directory.GetDirectories(Feed).SelectMany(Directory.GetDirectories))
        {
            var isSite = versionDir.Contains("portal-web");
            using var zip = ZipFile.Open(Path.Combine(versionDir, isSite ? "site.zip" : "app.zip"), ZipArchiveMode.Create);
            using var w = new StreamWriter(zip.CreateEntry(isSite ? "index.html" : "app.dll").Open());
            w.Write(isSite ? "<html>portal</html>" : "binary");
        }
    }

    public string Read(string path) => File.ReadAllText(Path.Combine(Repo, path));
    public void Write(string path, string text)
    {
        var full = Path.Combine(Repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }
    public void Edit(string path, string oldText, string newText)
    {
        var text = Read(path);
        Assert.Contains(oldText, text, $"test setup: {path} doesn't contain the text to replace");
        Write(path, text.Replace(oldText, newText));
    }

    public void AddPackage(string component, string version, string requiresYaml = "")
    {
        var dir = Path.Combine(Feed, component, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "release-metadata.yaml"), $"component: {component}\nversion: {version}\n{requiresYaml}");
        using var zip = ZipFile.Open(Path.Combine(dir, "app.zip"), ZipArchiveMode.Create);
        zip.CreateEntry("app.dll");
    }

    public ReleaseRepo Load(Diagnostics? d = null)
    {
        d ??= new Diagnostics();
        return Loader.Load(new DirectorySource(Repo), d, Repo);
    }

    public MetadataCache Metadata() => new(new LocalFeed(Feed));

    /// <summary>Load and validate. With baseline, also run the change rules against it.</summary>
    public Diagnostics Validate(ReleaseRepo? baseline = null, DateOnly? today = null)
    {
        var d = new Diagnostics();
        var repo = Load(d);
        if (!d.HasErrors)
            Validator.Validate(repo, new DirectorySource(Repo), baseline, Metadata(), today ?? new DateOnly(2026, 11, 19), d);
        return d;
    }

    /// <summary>A snapshot of the repo as it is now, to use as the "base branch".</summary>
    public ReleaseRepo Snapshot() => Load();

    public static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public static class DiagnosticsAssert
{
    public static void HasError(this Diagnostics d, string contains) =>
        Assert.IsTrue(d.Errors.Any(e => e.Message.Contains(contains, StringComparison.Ordinal)),
            $"Expected an error containing \"{contains}\". Got:\n{Describe(d)}");

    public static void HasWarning(this Diagnostics d, string contains) =>
        Assert.IsTrue(d.Warnings.Any(e => e.Message.Contains(contains, StringComparison.Ordinal)),
            $"Expected a warning containing \"{contains}\". Got:\n{Describe(d)}");

    public static void IsClean(this Diagnostics d) =>
        Assert.IsFalse(d.HasErrors, $"Expected no errors. Got:\n{Describe(d)}");

    public static string Describe(Diagnostics d) => d.Items.Count == 0 ? "(nothing)" : string.Join("\n", d.Items);
}

/// <summary>
/// A pretend Azure. It answers `az` the way Azure would, and keeps track of what's running
/// where: deploying to a slot puts that version in the slot, swapping exchanges slot and
/// production. FakeHttp then serves /version from the same state, so relctl's checks see the
/// effect of its own commands.
/// </summary>
public sealed class FakeAzure : ICommandRunner
{
    public List<Command> Calls { get; } = [];
    public Dictionary<string, Dictionary<string, string>> Tags { get; } = [];
    /// <summary>host → version it serves. Hosts are "{app}.test" and "{app}-{slot}.test".</summary>
    public Dictionary<string, string> Running { get; } = [];
    /// <summary>Hosts whose /health/ready returns 500.</summary>
    public HashSet<string> Unhealthy { get; } = [];
    public Func<Command, CommandResult?>? Override { get; set; }
    /// <summary>The files in the folder each `upload-batch` uploaded, read at the time.</summary>
    public Dictionary<string, Dictionary<string, string>> Uploads { get; } = [];

    public IEnumerable<string> AzCalls => Calls.Where(c => c.File == "az").Select(c => string.Join(' ', c.Args));
    public IEnumerable<string> Mutations => Calls.Where(c => c.File == "az" && !c.ReadOnly).Select(c => string.Join(' ', c.Args));

    public void SetDeployed(Component c, string env, string version)
    {
        var t = c.Targets[env];
        Tags.TryAdd(t.ResourceId(c.Type), []);
        Tags[t.ResourceId(c.Type)][AzureTagState.VersionTag(c.Name)] = version;
        Running[$"{t.Name}.test"] = version;
        if (t.Slot is { } slot) Running[$"{t.Name}-{slot}.test"] = "0.0.0";
    }

    public CommandResult Run(Command command)
    {
        Calls.Add(command);
        if (Override?.Invoke(command) is { } overridden) return overridden;
        if (command.File is "bash" or "git") return ProcessRunner.Instance.Run(command with { Quiet = true });
        if (command.File != "az") return new CommandResult(127, "", $"{command.File}: not found");

        var a = command.Args.ToList();
        string Opt(string name) => a[a.IndexOf(name) + 1];
        string? OptOrNull(string name) => a.Contains(name) ? Opt(name) : null;
        var verb = string.Join(' ', a.TakeWhile(x => !x.StartsWith("--", StringComparison.Ordinal)));

        switch (verb)
        {
            case "resource show":
                return Ok(JsonSerializer.Serialize(Tags.GetValueOrDefault(Opt("--ids"))));
            case "tag update":
                var tags = Tags.TryGetValue(Opt("--resource-id"), out var existing) ? existing : Tags[Opt("--resource-id")] = [];
                foreach (var kv in a.SkipWhile(x => x != "--tags").Skip(1)) tags[kv.Split('=')[0]] = kv.Split('=', 2)[1];
                return Ok("");
            case "webapp show":
                return Ok(OptOrNull("--slot") is { } s ? $"{Opt("--name")}-{s}.test\n" : $"{Opt("--name")}.test\n");
            case "storage account show":
                return Ok($"https://{Opt("--name")}.test/\n");
            case "webapp deploy":
            case "functionapp deployment source config-zip":
            {
                var zip = OptOrNull("--src-path") ?? Opt("--src");
                var version = Path.GetFileName(Path.GetDirectoryName(zip))!; // .../<package>/<version>/app.zip
                Running[OptOrNull("--slot") is { } slot ? $"{Opt("--name")}-{slot}.test" : $"{Opt("--name")}.test"] = version;
                return Ok("");
            }
            case "webapp deployment slot swap":
            case "functionapp deployment slot swap":
            {
                var prod = $"{Opt("--name")}.test";
                var slot = $"{Opt("--name")}-{Opt("--slot")}.test";
                (Running[prod], Running[slot]) = (Running.GetValueOrDefault(slot, "?"), Running.GetValueOrDefault(prod, "?"));
                var (prodSick, slotSick) = (Unhealthy.Contains(prod), Unhealthy.Contains(slot));
                Unhealthy.Remove(prod); Unhealthy.Remove(slot);
                if (slotSick) Unhealthy.Add(prod);
                if (prodSick) Unhealthy.Add(slot);
                return Ok("");
            }
            case "storage blob upload-batch":
            {
                var dir = Opt("--source");
                Uploads[Opt("--account-name")] = Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), f => File.ReadAllText(f));
                var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "config.json")));
                Running[$"{Opt("--account-name")}.test"] = config.RootElement.GetProperty("version").GetString()!;
                return Ok("");
            }
            default:
                return Ok("");
        }

        static CommandResult Ok(string stdout) => new(0, stdout, "");
    }
}

/// <summary>Serves /version, /api/version, /config.json and /health/ready from FakeAzure's state.</summary>
public sealed class FakeHttp(FakeAzure azure) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add(uri.ToString());
        var host = uri.Host;
        if (!azure.Running.TryGetValue(host, out var version))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }); // external URLs
        var response = uri.AbsolutePath switch
        {
            "/version" or "/api/version" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(version) },
            "/config.json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"version":"{{version}}"}""") },
            "/health/ready" => new HttpResponseMessage(azure.Unhealthy.Contains(host) ? HttpStatusCode.InternalServerError : HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        return Task.FromResult(response);
    }
}
