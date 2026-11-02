#!/usr/bin/env dotnet
// Tests for validate-manifest.cs. Run with:  dotnet run test-manifest.cs
//
// These are black-box tests: each case runs the validator against one manifest, the real one or
// a sample in test/fixtures/, and checks the exit code and the output. Each fixture breaks
// exactly one rule, and its header comment says which. That doubles as a catalogue of every
// mistake a manifest can make.

#:property PublishAot=false

using System.Diagnostics;

var dir = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var validator = Path.Combine(dir, "validate-manifest.cs");
const int Valid = 0, Invalid = 1;
const string OneError = "breaks 1 rule(s)";

Case[] cases =
[
    new("the real manifest is valid and runs in the expected waves", "../releases/release.yml", Valid,
        "1. deploy identity-api 1.5.2  |  deploy catalog-api 3.0.4",
        "2. deploy customers-api 1.2.0",
        "3. PAUSE: Run the billing-profile backfill (staging, prod)",
        "4. deploy payments-api 2.3.1",
        "5. deploy orders-api 1.8.0",
        "6. PAUSE: Smoke test the checkout journey (prod)",
        "Release 2026.11.04: test → staging → prod"),   // environment names read from the release template

    new("B deployed without depending on A is rejected, with the reason and the fix", "payments-before-customers.yml", Invalid,
        OneError,
        "payments-api must be deployed after customers-api",
        "Why: Calls GET /customers/{id}/billing-profile",
        "Fix: add deploy_customers to deploy_payments's dependsOn."),

    new("a dependency pinned below its minVersion is rejected even when the order is right", "customers-too-old.yml", Invalid,
        OneError, "payments-api 2.3.1 needs customers-api >= 1.2.0, but this release pins customers-api 1.1.4."),

    new("versions compare as numbers, so 10.0.0 satisfies >= 3.0.0", "catalog-major-10.yml", Valid,
        "deploy catalog-api 10.0.0"),

    new("every API must be listed", "orders-missing.yml", Invalid, OneError, "orders-api is missing."),
    new("an API can't be listed twice", "api-listed-twice.yml", Invalid, OneError, "orders-api is already deployed by step \"deploy_orders\""),
    new("an unknown API is rejected", "unknown-api.yml", Invalid, OneError, "unknown API \"search-api\""),
    new("versions must be exact", "version-not-exact.yml", Invalid, OneError, "must be an exact package version like 1.8.0, got \"latest\""),
    new("a typo in a key is an error, not silently ignored", "typo-in-key.yml", Invalid, OneError, "unknown key `depends_on`"),
    new("cycles are rejected and named", "cycle.yml", Invalid, OneError, "cycle", "deploy_identity → deploy_orders → deploy_payments → deploy_customers → deploy_identity"),
    new("dependsOn must point at a real step", "unknown-dependency.yml", Invalid, OneError, "dependsOn \"deploy_paymnets\", but there's no step with that id"),
    new("ids must be unique", "duplicate-id.yml", Invalid, OneError, "duplicate id \"deploy_identity\""),
    new("ids must be usable as Azure DevOps job names", "bad-id.yml", Invalid, OneError, "letters, digits and _"),
    new("manual steps need instructions", "manual-without-instructions.yml", Invalid, OneError, "manual steps need `instructions`"),
    new("manual steps only name real environments", "manual-unknown-environment.yml", Invalid, OneError, "unknown environment \"production\". Environments: test, staging, prod."),
    new("a step is a deploy or a pause, not both", "deploy-and-manual.yml", Invalid, OneError, "must have exactly one of `deploy`"),
];

// Every fixture must have a test, so a new sample manifest can't sit there unchecked.
var covered = cases.Select(c => Path.GetFileName(c.Manifest)).ToHashSet();
var untested = Directory.GetFiles(Path.Combine(dir, "test/fixtures"), "*.yml").Select(Path.GetFileName).Where(f => !covered.Contains(f!)).ToList();

// Build once, then run each case without rebuilding.
if (Run("build", validator).ExitCode != 0) { Console.Error.WriteLine("✗ validate-manifest.cs doesn't build"); return 1; }

var failures = 0;
foreach (var c in cases)
{
    var path = c.Manifest.StartsWith("..") ? Path.Combine(dir, c.Manifest) : Path.Combine(dir, "test/fixtures", c.Manifest);
    var result = Run("run", "--no-build", validator, "--", "--manifest", path);
    var problems = new List<string>();
    if (result.ExitCode != c.ExpectedExit) problems.Add($"exit code {result.ExitCode}, expected {c.ExpectedExit}");
    problems.AddRange(c.ExpectedOutput.Where(e => !result.Output.Contains(e)).Select(e => $"output is missing: {e}"));

    Console.WriteLine($"{(problems.Count == 0 ? "✓" : "✗")} {c.Name}");
    if (problems.Count == 0) continue;
    failures++;
    foreach (var p in problems) Console.WriteLine($"    {p}");
    Console.WriteLine($"    actual output:\n{Indent(result.Output)}");
}
foreach (var f in untested) { failures++; Console.WriteLine($"✗ test/fixtures/{f} has no test case"); }

Console.WriteLine($"\n{cases.Length - failures + untested.Count} of {cases.Length} passed{(untested.Count > 0 ? $", {untested.Count} fixture(s) untested" : "")}");
return failures == 0 ? 0 : 1;

static (int ExitCode, string Output) Run(params string[] args)
{
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    return (p.ExitCode, stdout.Result + stderr.Result);
}

static string Indent(string s) => string.Join('\n', s.TrimEnd().Split('\n').Select(l => "      " + l));

record Case(string Name, string Manifest, int ExpectedExit, params string[] ExpectedOutput);
