using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Relctl;

/// <summary>Where the release repo's files come from: a folder, or a git ref (the PR's base branch).</summary>
public interface IFileSource
{
    string Describe { get; }
    IEnumerable<string> List(string dir, string extension);
    string? Read(string path);
}

public sealed class DirectorySource(string root) : IFileSource
{
    public string Root { get; } = Path.GetFullPath(root);
    public string Describe => Root;

    public IEnumerable<string> List(string dir, string extension)
    {
        var full = Path.Combine(Root, dir);
        return Directory.Exists(full)
            ? Directory.GetFiles(full, "*" + extension).Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/')).Order()
            : [];
    }

    public string? Read(string path)
    {
        var full = Path.Combine(Root, path);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }
}

/// <summary>Reads files as they were at a git ref, without checking it out.</summary>
public sealed class GitRefSource(string repoDir, string gitRef) : IFileSource
{
    // The release repo might be a subfolder of the git repo (as it is in this sandbox).
    private readonly string _prefix = Git(repoDir, "rev-parse", "--show-prefix").Trim();

    public string Describe => $"{gitRef}";

    public IEnumerable<string> List(string dir, string extension) =>
        Git(repoDir, "ls-tree", "--name-only", $"{gitRef}:{_prefix}{dir}/", allowFail: true)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(n => n.EndsWith(extension, StringComparison.Ordinal))
            .Select(n => $"{dir}/{n}")
            .Order();

    public string? Read(string path)
    {
        var text = Git(repoDir, "show", $"{gitRef}:{_prefix}{path}", allowFail: true);
        return text.Length == 0 ? null : text;
    }

    private static string Git(string dir, string a, string b, bool allowFail = false) => Git(dir, [a, b], allowFail);
    private static string Git(string dir, string a, string b, string c, bool allowFail = false) => Git(dir, [a, b, c], allowFail);

    private static string Git(string dir, string[] args, bool allowFail)
    {
        var result = ProcessRunner.Instance.Run(new Command("git", args) { WorkingDirectory = dir, ReadOnly = true, Quiet = true });
        if (result.ExitCode != 0 && !allowFail)
            throw new RelctlException($"git {string.Join(' ', args)} failed: {result.StdErr.Trim()}");
        return result.ExitCode == 0 ? result.StdOut : "";
    }
}

public static partial class Loader
{
    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidPattern();

    public static ReleaseRepo Load(IFileSource source, Diagnostics d, string root = "")
    {
        var policy = LoadPolicy(source, d);

        var catalog = new Dictionary<string, Component>();
        foreach (var file in source.List("components", ".yaml"))
            if (LoadComponent(source.Read(file)!, file, d, policy) is { } c)
            {
                if (catalog.ContainsKey(c.Name)) d.Error(file, 1, $"component \"{c.Name}\" is defined twice (also in {catalog[c.Name].File}).");
                else catalog[c.Name] = c;
            }

        var environments = new Dictionary<string, EnvironmentManifest>();
        foreach (var file in source.List("environments", ".yaml"))
            if (LoadEnvironment(source.Read(file)!, file, d) is { } e)
            {
                var expected = Path.GetFileNameWithoutExtension(file);
                if (e.Environment != expected)
                    d.Error(file, 1, $"says `environment: {e.Environment}`, but the file is named {expected}.yaml. They must match.");
                environments[e.Environment] = e;
            }

        return new ReleaseRepo(root, policy, catalog, environments);
    }

    /// <summary>Load a folder, failing loudly if it's broken. For commands that act rather than lint.</summary>
    public static ReleaseRepo LoadOrThrow(string dir)
    {
        var d = new Diagnostics();
        var repo = Load(new DirectorySource(dir), d, Path.GetFullPath(dir));
        if (d.HasErrors)
            throw new RelctlException($"The release repo has errors. Run `relctl validate` for details. First: {d.Errors.First()}");
        return repo;
    }

    internal static Policy LoadPolicy(IFileSource source, Diagnostics d)
    {
        const string file = "policy.yaml";
        var text = source.Read(file);
        if (text is null)
        {
            d.Error(file, 0, $"is missing from {source.Describe}.");
            return new Policy([], 5, null, file);
        }
        var r = MapReader.FromDocument(text, file, d, "the release policy");
        if (r is null) return new Policy([], 5, null, file);

        var maxWaves = r.Int("maxWaves", 5);
        FeedSettings? feed = null;
        if (r.Map("feed") is { } f)
        {
            var org = f.Str("organization", required: true);
            var project = f.Str("project");
            var name = f.Str("name", required: true);
            f.Done();
            if (org is not null && name is not null) feed = new FeedSettings(org, project, name);
        }

        var envs = new List<EnvironmentPolicy>();
        foreach (var node in r.Seq("environments"))
        {
            if (!r.IsMap(node, "each environment")) continue;
            var e = r.Child(node, "an environment");
            var name = e.Str("name", required: true);
            var from = e.Str("promotesFrom");
            var cr = e.Bool("changeRequest", false);
            var freezes = new List<Freeze>();
            foreach (var fz in e.Seq("freezes"))
            {
                if (!e.IsMap(fz, "each freeze")) continue;
                var z = e.Child(fz, "a freeze");
                var fromDate = Date(z, "from");
                var toDate = Date(z, "to");
                var reason = z.Str("reason", required: true);
                z.Done();
                if (fromDate is { } a && toDate is { } b && reason is not null)
                {
                    if (b < a) d.Error(file, z.Line, $"freeze \"{reason}\" ends ({b:yyyy-MM-dd}) before it starts ({a:yyyy-MM-dd}).");
                    freezes.Add(new Freeze(a, b, reason));
                }
            }
            e.Done();
            if (name is not null) envs.Add(new EnvironmentPolicy(name, from, cr, freezes, e.Line));
        }
        r.Done();

        if (envs.Count == 0) d.Error(file, r.Line, "lists no `environments`.");
        foreach (var e in envs)
        {
            if (envs.Count(x => x.Name == e.Name) > 1) d.Error(file, e.Line, $"environment \"{e.Name}\" is listed twice.");
            if (e.PromotesFrom is { } up && envs.All(x => x.Name != up))
                d.Error(file, e.Line, $"\"{e.Name}\" promotes from \"{up}\", which isn't an environment here.");
        }
        // The promotion chain must end somewhere, or nothing could ever be deployed.
        foreach (var e in envs)
        {
            var seen = new HashSet<string>();
            for (var cur = e; cur?.PromotesFrom is { } up; cur = envs.FirstOrDefault(x => x.Name == up))
                if (!seen.Add(cur.Name)) { d.Error(file, e.Line, $"promotesFrom goes round in a circle from \"{e.Name}\"."); break; }
        }
        return new Policy(envs, maxWaves, feed, file);
    }

    private static DateOnly? Date(MapReader r, string key)
    {
        var text = r.Str(key, required: true);
        if (text is null) return null;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        r.Diagnostics.Error(r.File, r.LineOf(key), $"`{key}` should be a date like 2026-12-20, not \"{text}\".");
        return null;
    }

    internal static Component? LoadComponent(string text, string file, Diagnostics d, Policy policy)
    {
        var r = MapReader.FromDocument(text, file, d, "a component");
        if (r is null) return null;

        var name = r.Str("name", required: true);
        var typeText = r.Str("type", required: true);
        var package = r.Str("package");
        var owner = r.Str("owner");
        var versionPath = r.Str("versionPath");
        var hooks = LoadHooks(r);
        var targetsMap = r.Map("targets", required: true);
        r.Done();

        if (name is null || typeText is null) return null;
        if (!NamePattern().IsMatch(name))
            d.Error(file, r.LineOf("name"), $"name \"{name}\" should be lower-case letters, digits and hyphens.");
        if (Path.GetFileNameWithoutExtension(file) != name)
            d.Error(file, r.LineOf("name"), $"says `name: {name}`, but the file is named {Path.GetFileName(file)}. They must match.");
        if (!ComponentTypes.ByName.TryGetValue(typeText, out var type))
        {
            d.Error(file, r.LineOf("type"), $"type \"{typeText}\" isn't one relctl can deploy. Types: {string.Join(", ", ComponentTypes.ByName.Keys)}.");
            return null;
        }
        if (versionPath is not null && !versionPath.StartsWith('/'))
            d.Error(file, r.LineOf("versionPath"), $"versionPath should start with /, like /version.");
        foreach (var h in hooks.All)
            if (h.Http is { } http && !http.StartsWith('/') && !http.StartsWith("https://", StringComparison.Ordinal))
                d.Error(file, h.Line, $"hook \"{h.Name}\": `http` should be a path on the app (/health) or an https:// URL.");

        var targets = new Dictionary<string, Target>();
        if (targetsMap is not null)
        {
            foreach (var (env, node, line) in targetsMap.Entries())
            {
                if (!targetsMap.IsMap(node, $"target \"{env}\"")) continue;
                var t = targetsMap.Child(node, $"target \"{env}\"");
                var sub = t.Str("subscription", required: true);
                var rg = t.Str("resourceGroup", required: true);
                var resource = t.Str(type == ComponentType.StaticSite ? "storageAccount" : "app", required: true);
                var slot = type == ComponentType.StaticSite ? null : t.Str("slot");
                var config = new Dictionary<string, string>();
                if (type == ComponentType.StaticSite && t.Map("config") is { } cfg)
                    foreach (var (k, v, l) in cfg.Entries())
                    {
                        if (v is YamlScalarNode s) config[k] = s.Value ?? "";
                        else d.Error(file, l, $"config value `{k}` should be a single value.");
                    }
                t.Done();

                if (policy.Environments.Count > 0 && policy.For(env) is null)
                    d.Error(file, line, $"has a target for \"{env}\", which isn't an environment in policy.yaml.");
                if (sub is not null && !GuidPattern().IsMatch(sub))
                    d.Error(file, t.LineOf("subscription"), $"subscription should be the subscription ID (a GUID), not \"{sub}\". Resource IDs need it.");
                if (sub is not null && rg is not null && resource is not null)
                    targets[env] = new Target(env, sub, rg, resource, slot, config, line);
            }
        }

        return new Component(name, type, package ?? name, owner, versionPath, targets, hooks, file);
    }

    internal static HookSet LoadHooks(MapReader parent)
    {
        var map = parent.Map("hooks");
        if (map is null) return HookSet.Empty;
        var pre = LoadHookList(map, "preSync");
        var post = LoadHookList(map, "postSync");
        map.Done();
        return new HookSet(pre, post);
    }

    private static List<Hook> LoadHookList(MapReader map, string key)
    {
        var hooks = new List<Hook>();
        foreach (var node in map.Seq(key))
        {
            if (!map.IsMap(node, $"each {key} hook")) continue;
            var h = map.Child(node, $"a {key} hook");
            var name = h.Str("name", required: true);
            var script = h.Str("script");
            var http = h.Str("http");
            var release = h.Str("release");
            var timeout = h.Int("timeoutSeconds", 600);
            h.Done();
            if (name is null) continue;
            if ((script is null) == (http is null))
            {
                h.Diagnostics.Error(h.File, h.Line, $"hook \"{name}\" needs exactly one of `script` or `http`.");
                continue;
            }
            hooks.Add(new Hook(name, script, http, release, timeout, h.Line));
        }
        return hooks;
    }

    internal static EnvironmentManifest? LoadEnvironment(string text, string file, Diagnostics d)
    {
        var r = MapReader.FromDocument(text, file, d, "an environment manifest");
        if (r is null) return null;

        var env = r.Str("environment", required: true);
        var release = r.Str("release");
        var componentsMap = r.Map("components");
        var pausesNodes = r.Seq("pauses");
        var hooks = LoadHooks(r);
        r.Done();
        if (env is null) return null;

        var components = new Dictionary<string, DesiredComponent>();
        foreach (var (name, node, line) in componentsMap?.Entries() ?? [])
        {
            // Two forms:  `orders-api: 1.8.0`  or  `orders-api: { version: 1.7.3, rollback: "INC123: ..." }`
            string? versionText;
            string? rollback = null;
            if (node is YamlScalarNode s) versionText = s.Value;
            else if (node is YamlMappingNode)
            {
                var m = componentsMap!.Child(node, $"\"{name}\"");
                versionText = m.Str("version", required: true);
                rollback = m.Str("rollback", required: true);
                m.Done();
            }
            else
            {
                d.Error(file, line, $"\"{name}\" should be a version (1.2.3) or {{ version, rollback }}.");
                continue;
            }
            if (!SemVer.TryParse(versionText, out var version))
            {
                d.Error(file, line, $"\"{name}\" version \"{versionText}\" isn't an exact x.y.z version. Ranges, `latest` and pre-release tags aren't allowed: a manifest names exactly one build.");
                continue;
            }
            components[name] = new DesiredComponent(name, version.Value, rollback, line);
        }

        var pauses = new List<Pause>();
        foreach (var node in pausesNodes)
        {
            if (!r.IsMap(node, "each pause")) continue;
            var p = r.Child(node, "a pause");
            var before = p.Str("before", required: true);
            var forRelease = p.Str("release", required: true);
            var instructions = p.Str("instructions", required: true);
            p.Done();
            if (before is not null && instructions is not null)
                pauses.Add(new Pause(before, forRelease, instructions.Trim(), p.Line));
        }

        return new EnvironmentManifest(env, release, components, pauses, hooks, file);
    }

    internal static PackageMetadata? LoadMetadata(string text, string file, Diagnostics d)
    {
        var r = MapReader.FromDocument(text, file, d, "package metadata");
        if (r is null) return null;
        var component = r.Str("component", required: true);
        var versionText = r.Str("version", required: true);
        var build = r.Map("build");
        string? commit = null, buildId = null;
        if (build is not null)
        {
            commit = build.Str("commit");
            buildId = build.Str("run");
            build.Str("branch");
            build.Done();
        }
        var requires = new List<Requirement>();
        foreach (var node in r.Seq("requires"))
        {
            if (!r.IsMap(node, "each requirement")) continue;
            var q = r.Child(node, "a requirement");
            var other = q.Str("component", required: true);
            var min = q.Str("minVersion", required: true);
            var reason = q.Str("reason", required: true);
            q.Done();
            if (other is null || reason is null) continue;
            if (!SemVer.TryParse(min, out var minVersion))
            {
                d.Error(file, q.Line, $"minVersion \"{min}\" isn't an exact x.y.z version.");
                continue;
            }
            requires.Add(new Requirement(other, minVersion.Value, reason));
        }
        r.Done();
        if (component is null || !SemVer.TryParse(versionText, out var version)) return null;
        return new PackageMetadata(component, version.Value, commit, buildId, requires);
    }
}
