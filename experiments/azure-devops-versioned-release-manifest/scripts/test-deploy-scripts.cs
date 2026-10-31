#!/usr/bin/env dotnet
// Tests for the deploy scripts in ../pipelines/scripts. Run with:  dotnet run test-deploy-scripts.cs
//
// The scripts run for real, against the real orders-api running on localhost. Only Azure is
// faked: a stand-in `az` on PATH answers `az webapp show ... --query defaultHostName` with the
// local address of either "production" (orders-api 1.8.0) or the "staging slot" (1.8.1), and
// logs how it was called.
//
// The demo APIs target .NET 8. DOTNET_ROLL_FORWARD=Major lets them run on whatever newer
// runtime is installed, so the .NET 10 SDK is all these tests need.

#:property PublishAot=false

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

var dir = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var root = Path.GetFullPath(Path.Combine(dir, ".."));
var scripts = Path.Combine(root, "pipelines/scripts");
var temp = Directory.CreateTempSubdirectory("deploy-script-tests-").FullName;
var apps = new List<Process>();

try
{
    Console.WriteLine("Publishing orders-api 1.8.0 (production) and 1.8.1 (staging slot)...");
    var v180 = Publish("1.8.0");
    var v181 = Publish("1.8.1");
    int prodPort = FreePort(), slotPort = FreePort(), closedPort = FreePort();
    apps.Add(await StartApi(v180, prodPort));
    apps.Add(await StartApi(v181, slotPort));

    // The fake az. Production and slot ports come from the environment, so each test can point
    // them somewhere else (such as a port nothing is listening on).
    var bin = Directory.CreateDirectory(Path.Combine(temp, "bin")).FullName;
    var azLog = Path.Combine(temp, "az.log");
    File.WriteAllText(Path.Combine(bin, "az"), $$"""
        #!/usr/bin/env bash
        echo "$*" >> '{{azLog}}'
        [ -n "${FAKE_AZ_FAIL:-}" ] && { echo "ERROR: (ResourceNotFound) fake failure" >&2; exit 1; }
        if [[ " $* " == *" --slot "* ]]; then echo "127.0.0.1:$FAKE_SLOT_PORT"; else echo "127.0.0.1:$FAKE_PROD_PORT"; fi
        """);
    if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These tests run bash scripts, so they need Linux or macOS.");
    File.SetUnixFileMode(Path.Combine(bin, "az"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    (int ExitCode, string Output) Script(string name, string[] args, Dictionary<string, string>? env = null)
    {
        File.Delete(azLog);
        var vars = new Dictionary<string, string>
        {
            ["PATH"] = $"{bin}:{Environment.GetEnvironmentVariable("PATH")}",
            ["VERSION_URL_SCHEME"] = "http",
            ["FAKE_PROD_PORT"] = prodPort.ToString(),
            ["FAKE_SLOT_PORT"] = slotPort.ToString(),
            ["INTERVAL_SECONDS"] = "0",
        };
        foreach (var (k, v) in env ?? []) vars[k] = v;
        return Run("bash", [Path.Combine(scripts, name), .. args], vars);
    }
    string AzCalls() => File.Exists(azLog) ? File.ReadAllText(azLog) : "";
    string[] App(params string[] rest) => ["rg-apis-test", "app-orders-api-test", .. rest];
    const string UpToDate = "##vso[task.setvariable variable=upToDate]true";

    var t = new Tests();

    t.Check("check-live-version: skips the deploy when live already runs the target version", () =>
    {
        var r = Script("check-live-version.sh", App("1.8.0"));
        return [Expect.Exit(r, 0), Expect.Contains(r, "app-orders-api-test is running 1.8.0."), Expect.Contains(r, UpToDate),
                Expect.Text(AzCalls(), "webapp show -g rg-apis-test -n app-orders-api-test --query defaultHostName -o tsv")];
    });

    t.Check("check-live-version: deploys when live runs a different version", () =>
    {
        var r = Script("check-live-version.sh", App("1.8.1"));
        return [Expect.Exit(r, 0), Expect.Contains(r, "is running 1.8.0. This release wants 1.8.1."), Expect.Missing(r, UpToDate)];
    });

    t.Check("check-live-version: deploys when live is unreachable", () =>
    {
        var r = Script("check-live-version.sh", App("1.8.0"), new() { ["FAKE_PROD_PORT"] = closedPort.ToString() });
        return [Expect.Exit(r, 0), Expect.Contains(r, "is running unreachable."), Expect.Missing(r, UpToDate)];
    });

    t.Check("check-live-version: fails the job when az fails (bad service connection, missing app)", () =>
    {
        var r = Script("check-live-version.sh", App("1.8.0"), new() { ["FAKE_AZ_FAIL"] = "1" });
        return [Expect.NonZeroExit(r), Expect.Missing(r, UpToDate)];
    });

    t.Check("wait-for-version: succeeds once the slot reports the new version, and asks az for the slot", () =>
    {
        var r = Script("wait-for-version.sh", App("1.8.1", "staging"));
        return [Expect.Exit(r, 0), Expect.Contains(r, "The 'staging' slot is serving 1.8.1"),
                Expect.Text(AzCalls(), "webapp show -g rg-apis-test -n app-orders-api-test --slot staging")];
    });

    t.Check("wait-for-version: gives up after ATTEMPTS and says production is untouched", () =>
    {
        var r = Script("wait-for-version.sh", App("1.8.2", "staging"), new() { ["ATTEMPTS"] = "3" });
        return [Expect.Exit(r, 1), Expect.Contains(r, "Attempt 3 of 3: the 'staging' slot reports 1.8.1"),
                Expect.Contains(r, "##vso[task.logissue type=error]The 'staging' slot never reported 1.8.2. Production is untouched.")];
    });

    await t.CheckAsync("wait-for-version: keeps polling while the slot restarts", async () =>
    {
        // Nothing is listening on the slot port when the script starts. The new version comes up 3s later.
        var latePort = FreePort();
        var script = Task.Run(() => Script("wait-for-version.sh", App("1.8.1", "staging"),
            new() { ["FAKE_SLOT_PORT"] = latePort.ToString(), ["ATTEMPTS"] = "30", ["INTERVAL_SECONDS"] = "1" }));
        await Task.Delay(3000);
        apps.Add(await StartApi(v181, latePort));
        var r = await script;
        return [Expect.Exit(r, 0), Expect.Contains(r, "Attempt 1 of 30: the 'staging' slot reports unreachable"),
                Expect.Contains(r, "The 'staging' slot is serving 1.8.1")];
    });

    t.Check("verify-production: passes when production reports the new version", () =>
    {
        var r = Script("verify-production.sh", App("1.8.0", "staging"));
        return [Expect.Exit(r, 0), Expect.Contains(r, "Production is serving app-orders-api-test 1.8.0")];
    });

    t.Check("verify-production: fails with the swap-back command when production reports anything else", () =>
    {
        var r = Script("verify-production.sh", App("1.8.1", "staging"));
        return [Expect.Exit(r, 1), Expect.Contains(r, "Production reports 1.8.0 after the swap, expected 1.8.1."),
                Expect.Contains(r, "az webapp deployment slot swap -g 'rg-apis-test' -n 'app-orders-api-test' --slot 'staging'")];
    });

    return t.Finish();
}
finally
{
    foreach (var app in apps) { try { app.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    Directory.Delete(temp, recursive: true);
}

string Publish(string version)
{
    var outDir = Path.Combine(temp, $"orders-api-{version}");
    var r = Run("dotnet", ["publish", Path.Combine(root, "apis/orders-api/OrdersApi.csproj"), "-c", "Release", "-o", outDir, $"-p:Version={version}", "--nologo", "-v", "quiet"]);
    if (r.ExitCode != 0) throw new Exception($"dotnet publish failed:\n{r.Output}");
    return outDir;
}

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    var port = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}

static async Task<Process> StartApi(string publishDir, int port)
{
    var psi = new ProcessStartInfo("dotnet", [Path.Combine(publishDir, "OrdersApi.dll")])
    {
        RedirectStandardOutput = true, RedirectStandardError = true,
        Environment = { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}", ["DOTNET_ROLL_FORWARD"] = "Major" },
    };
    var p = Process.Start(psi)!;
    p.OutputDataReceived += (_, _) => { };
    p.ErrorDataReceived += (_, _) => { };
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
    for (var i = 0; i < 60; i++)
    {
        try { if ((await http.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode) return p; }
        catch (HttpRequestException) { }
        await Task.Delay(250);
    }
    throw new Exception($"orders-api didn't start on port {port}");
}

static (int ExitCode, string Output) Run(string file, string[] args, Dictionary<string, string>? env = null)
{
    var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in args) psi.ArgumentList.Add(a);
    foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
    psi.Environment.Remove("HTTPS_PROXY");   // the scripts' curl must hit localhost directly
    psi.Environment.Remove("https_proxy");
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    return (p.ExitCode, stdout.Result + stderr.Result);
}

static class Expect
{
    public static string? Exit((int ExitCode, string Output) r, int code) => r.ExitCode == code ? null : $"exit code {r.ExitCode}, expected {code}";
    public static string? NonZeroExit((int ExitCode, string Output) r) => r.ExitCode != 0 ? null : "exit code 0, expected a failure";
    public static string? Contains((int ExitCode, string Output) r, string text) => Text(r.Output, text);
    public static string? Missing((int ExitCode, string Output) r, string text) => r.Output.Contains(text) ? $"output should not contain: {text}" : null;
    public static string? Text(string haystack, string text) => haystack.Contains(text) ? null : $"missing: {text}";
}

sealed class Tests
{
    int passed, failed;

    public void Check(string name, Func<string?[]> test) => Report(name, test());

    public async Task CheckAsync(string name, Func<Task<string?[]>> test) => Report(name, await test());

    void Report(string name, string?[] results)
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
