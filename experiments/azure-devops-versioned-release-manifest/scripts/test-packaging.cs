#!/usr/bin/env dotnet
// Tests for packaging, from build to release validation. Run with:  dotnet run test-packaging.cs
//
// Builds every API's package with ../pipelines/scripts/assemble-package.sh, the same script the
// build pipeline runs, at the versions in ../releases/release.yml. Then it checks:
//   - each package holds exactly app.zip, release-rules.yaml and build-info.json
//   - each app.zip really runs and reports its package version on /version
//   - the validator accepts the release using the rules INSIDE the packages
//   - rules are per version: a newer package's own rules win over the repo's current ones
// Only the Azure Artifacts feed is missing: packages go to a temp folder laid out the way the
// release pipeline downloads them (packages/<api>/...).
//
// The demo APIs target .NET 8. DOTNET_ROLL_FORWARD=Major lets them run on whatever newer
// runtime is installed, so the .NET 10 SDK is all these tests need.

#:package YamlDotNet@16.3.0
#:property PublishAot=false

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using YamlDotNet.Serialization;

var dir = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var root = Path.GetFullPath(Path.Combine(dir, ".."));
var assemble = Path.Combine(root, "pipelines/scripts/assemble-package.sh");
var validator = Path.Combine(dir, "validate-manifest.cs");
var temp = Directory.CreateTempSubdirectory("packaging-tests-").FullName;
var buildVars = new Dictionary<string, string>
{
    ["BUILD_SOURCEVERSION"] = "3f2c9e1",
    ["BUILD_SOURCEBRANCH"] = "refs/heads/main",
    ["BUILD_BUILDID"] = "4242",
    ["SYSTEM_COLLECTIONURI"] = "https://dev.azure.com/contoso/",
    ["SYSTEM_TEAMPROJECT"] = "Platform",
};
var t = new Tests();

try
{
    var release = ReleaseVersions(Path.Combine(root, "releases/release.yml"));
    var packages = Path.Combine(temp, "packages");

    foreach (var (api, version) in release)
    {
        var outDir = Path.Combine(packages, api);
        var built = Run("bash", [assemble, api, Project(Path.Combine(root, "apis", api)), version, outDir], buildVars);
        t.Check($"{api} {version}: assembles", [built.ExitCode == 0 ? null : $"assemble-package.sh failed:\n{built.Output}"]);
        if (built.ExitCode != 0) continue;

        var files = Directory.GetFiles(outDir).Select(Path.GetFileName).Order().ToArray();
        var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "build-info.json"))).RootElement;
        t.Check($"{api} {version}: package holds the app, its release rules and its build info",
        [
            Same(string.Join(", ", files), "app.zip, build-info.json, release-rules.yaml", "files"),
            File.ReadAllText(Path.Combine(outDir, "release-rules.yaml")) == File.ReadAllText(Path.Combine(root, "apis", api, "release-rules.yaml"))
                ? null : "release-rules.yaml differs from the source",
            Same(info.GetProperty("api").GetString(), api, "build-info api"),
            Same(info.GetProperty("version").GetString(), version, "build-info version"),
            Same(info.GetProperty("commit").GetString(), "3f2c9e1", "build-info commit"),
            Same(info.GetProperty("buildUrl").GetString(), "https://dev.azure.com/contoso/Platform/_build/results?buildId=4242", "build-info buildUrl"),
        ]);

        var reported = await RunPackagedApp(Path.Combine(outDir, "app.zip"), Path.Combine(temp, "run", api));
        t.Check($"{api} {version}: app.zip runs and reports its package version on /version",
            [Same(reported, $$"""{"api":"{{api}}","version":"{{version}}"}""", "/version")]);
    }

    var valid = Run("dotnet", ["run", validator, "--", "--rules-dir", packages]);
    t.Check("the release validates against the rules inside the packages",
        [valid.ExitCode == 0 ? null : $"validator failed:\n{valid.Output}", Has(valid.Output, "4. deploy payments-api 2.3.1")]);

    // payments-api 2.4.0 has dropped its customers-api dependency. Its source, and so its
    // package, carries the new rules. customers-api 1.1.4 is an older build of customers-api.
    var next = Path.Combine(temp, "packages-next");
    CopyDir(packages, next);
    var payments240 = CopySource("payments-api", Path.Combine(temp, "src-2.4.0"));
    File.WriteAllText(Path.Combine(payments240, "release-rules.yaml"), """
        # payments-api 2.4.0 no longer calls GET /customers/{id}/billing-profile.
        api: payments-api
        deployAfter: []
        """);
    var rebuilt = new[]
    {
        Run("bash", [assemble, "payments-api", Project(payments240), "2.4.0", Path.Combine(next, "payments-api")], buildVars),
        Run("bash", [assemble, "customers-api", Project(Path.Combine(root, "apis/customers-api")), "1.1.4", Path.Combine(next, "customers-api")], buildVars),
    };
    var manifest = Path.Combine(dir, "test/packaging/payments-drops-dependency.yml");
    var withPackaged = Run("dotnet", ["run", validator, "--", "--manifest", manifest, "--rules-dir", next]);
    var withRepo = Run("dotnet", ["run", validator, "--", "--manifest", manifest]);
    t.Check("rules are per version: payments-api 2.4.0's packaged rules allow customers-api 1.1.4",
    [
        .. rebuilt.Select(r => r.ExitCode == 0 ? null : $"assemble failed:\n{r.Output}"),
        withPackaged.ExitCode == 0 ? null : $"validator should accept it with the packaged rules:\n{withPackaged.Output}",
    ]);
    t.Check("...while the repo's current payments-api rules would reject the same release",
        [withRepo.ExitCode == 1 ? null : $"exit code {withRepo.ExitCode}, expected 1", Has(withRepo.Output, "payments-api 2.4.0 needs customers-api >= 1.2.0")]);

    // A rules file copied from another API and not edited would publish wrong rules.
    var mislabelled = CopySource("orders-api", Path.Combine(temp, "src-mislabelled"));
    File.WriteAllText(Path.Combine(mislabelled, "release-rules.yaml"), "api: payments-api\ndeployAfter: []\n");
    var badOut = Path.Combine(temp, "packages-bad/orders-api");
    var bad = Run("bash", [assemble, "orders-api", Project(mislabelled), "1.8.0", badOut], buildVars);
    t.Check("assemble-package.sh refuses a rules file that names a different API",
    [
        bad.ExitCode != 0 ? null : "exit code 0, expected a failure",
        Has(bad.Output, "declares api 'payments-api', but this is the orders-api package"),
        Directory.Exists(badOut) ? "it still produced a package folder" : null,
    ]);

    return t.Finish();
}
finally
{
    Directory.Delete(temp, recursive: true);
}

// (api, version) for every deploy step in the manifest.
static List<(string Api, string Version)> ReleaseVersions(string manifestPath)
{
    var doc = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifestPath));
    var steps = (List<object>)((Dictionary<object, object>)((Dictionary<object, object>)doc["extends"])["parameters"])["steps"];
    return steps.Cast<Dictionary<object, object>>()
        .Where(s => s.ContainsKey("deploy"))
        .Select(s => ((string)s["deploy"], (string)s["version"]))
        .ToList();
}

static string Project(string apiDir) => Directory.GetFiles(apiDir, "*.csproj").Single();

string CopySource(string api, string intoDir)
{
    var dest = Path.Combine(intoDir, api);
    CopyDir(Path.Combine(root, "apis", api), dest);
    return dest;
}

static void CopyDir(string from, string to)
{
    foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(from, file);
        if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")) continue;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(to, rel))!);
        File.Copy(file, Path.Combine(to, rel));
    }
}

// Unzips the package's app.zip, runs it the way App Service would, and returns its /version body.
static async Task<string> RunPackagedApp(string zip, string into)
{
    ZipFile.ExtractToDirectory(zip, into);
    var dll = Directory.GetFiles(into, "*.runtimeconfig.json").Single().Replace(".runtimeconfig.json", ".dll");
    var port = FreePort();
    var psi = new ProcessStartInfo("dotnet", [dll])
    {
        RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = into,
        Environment = { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}", ["DOTNET_ROLL_FORWARD"] = "Major" },
    };
    using var app = Process.Start(psi)!;
    app.OutputDataReceived += (_, _) => { };
    app.ErrorDataReceived += (_, _) => { };
    app.BeginOutputReadLine();
    app.BeginErrorReadLine();
    try
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        for (var i = 0; i < 60; i++)
        {
            try { return await http.GetStringAsync($"http://127.0.0.1:{port}/version"); }
            catch (HttpRequestException) { await Task.Delay(250); }
        }
        return "(never started)";
    }
    finally { app.Kill(entireProcessTree: true); }
}

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    var port = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}

static (int ExitCode, string Output) Run(string file, string[] args, Dictionary<string, string>? env = null)
{
    var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in args) psi.ArgumentList.Add(a);
    foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    return (p.ExitCode, stdout.Result + stderr.Result);
}

static string? Same(string? actual, string expected, string what) => actual == expected ? null : $"{what}: got \"{actual}\", expected \"{expected}\"";
static string? Has(string output, string text) => output.Contains(text) ? null : $"output is missing: {text}\n{output}";

sealed class Tests
{
    int passed, failed;

    public void Check(string name, string?[] results)
    {
        var problems = results.OfType<string>().ToList();
        Console.WriteLine($"{(problems.Count == 0 ? "✓" : "✗")} {name}");
        foreach (var p in problems) Console.WriteLine($"    {p}");
        if (problems.Count == 0) passed++; else failed++;
    }

    public int Finish()
    {
        Console.WriteLine($"\n{passed} of {passed + failed} passed");
        return failed == 0 ? 0 : 1;
    }
}
