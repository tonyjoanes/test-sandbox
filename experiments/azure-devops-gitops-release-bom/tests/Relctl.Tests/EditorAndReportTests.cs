using System.Text.Json.Nodes;

namespace Relctl.Tests;

[TestClass]
public sealed class ManifestEditorTests
{
    private const string Manifest = """
        # Live. Owned by Release Managers.
        environment: live
        release: 2026.11.2

        components:
          customers-api: 1.1.4     # held back until the partner is ready
          orders-api:
            version: 1.7.2
            rollback: INC1 something
          payments-api: { version: 2.2.6, rollback: "INC2" }

        # Keep this comment.
        hooks:
          preSync: []
        """;

    [TestMethod]
    public void Setting_a_version_keeps_comments_and_layout()
    {
        var text = ManifestEditor.SetVersion(Manifest, "customers-api", SemVer.Parse("1.2.0"));
        Assert.AreEqual(Manifest.Replace("customers-api: 1.1.4", "customers-api: 1.2.0"), text);
    }

    [TestMethod]
    public void Setting_a_version_replaces_a_rollback_entry_in_either_form()
    {
        var text = ManifestEditor.SetVersion(Manifest, "orders-api", SemVer.Parse("1.8.0"));
        text = ManifestEditor.SetVersion(text, "payments-api", SemVer.Parse("2.3.1"));
        StringAssert.Contains(text, "  orders-api: 1.8.0\n  payments-api: 2.3.1\n\n# Keep this comment.");
        Assert.DoesNotContain("rollback", text);
    }

    [TestMethod]
    public void Setting_a_new_component_adds_it_to_the_block()
    {
        var text = ManifestEditor.SetVersion(Manifest, "invoices-api", SemVer.Parse("1.0.0"));
        StringAssert.Contains(text, "  payments-api: { version: 2.2.6, rollback: \"INC2\" }\n  invoices-api: 1.0.0\n\n# Keep this comment.");
        var d = new Diagnostics();
        Assert.IsNotNull(Loader.LoadEnvironment(text, "live.yaml", d));
        d.IsClean();
    }

    [TestMethod]
    public void Promote_copies_versions_and_the_release_name_and_nothing_else()
    {
        using var t = new TestRepo();
        var (text, changes, skipped) = ManifestEditor.Promote(t.Load(), "staging", "live", t.Read("environments/live.yaml"), null, null);
        CollectionAssert.AreEqual(new[] { "customers-api", "orders-api", "orders-func", "payments-api", "portal-web" }, changes.Select(c => c.Component).ToArray());
        Assert.IsEmpty(skipped);
        StringAssert.Contains(text, "release: 2026.11.3");
        StringAssert.Contains(text, "# Release-level hooks run once per sync", "comments survive");
        Assert.DoesNotContain("pauses:", text, "pauses are per environment; they aren't copied");
    }

    [TestMethod]
    public void Promote_can_take_just_one_component_for_a_hotfix()
    {
        using var t = new TestRepo();
        var (text, changes, _) = ManifestEditor.Promote(t.Load(), "uat", "staging", t.Read("environments/staging.yaml"), ["orders-api"], "2026.11.3-hf1");
        Assert.HasCount(1, changes);
        Assert.AreEqual("1.8.2", changes[0].To.ToString());
        StringAssert.Contains(text, "release: 2026.11.3-hf1");
    }
}

[TestClass]
public sealed class ReportTests
{
    [TestMethod]
    public void The_bom_is_cyclonedx_with_build_provenance_and_dependencies()
    {
        using var t = new TestRepo();
        var repo = t.Load();
        var bom = Reports.Bom(repo, repo.Env("staging"), t.Metadata(), "deadbeef", DateTimeOffset.UnixEpoch);

        Assert.AreEqual("CycloneDX", bom["bomFormat"]!.GetValue<string>());
        Assert.AreEqual("1.6", bom["specVersion"]!.GetValue<string>());
        Assert.AreEqual("2026.11.3", bom["metadata"]!["component"]!["version"]!.GetValue<string>());
        var components = bom["components"]!.AsArray();
        Assert.HasCount(6, components);
        var orders = components.Single(c => c!["name"]!.GetValue<string>() == "orders-api")!;
        Assert.AreEqual("orders-api@1.8.0", orders["bom-ref"]!.GetValue<string>());
        Assert.IsTrue(orders["properties"]!.AsArray().Any(p => p!["name"]!.GetValue<string>() == "relctl:build.commit"));
        var deps = bom["dependencies"]!.AsArray().Single(d => d!["ref"]!.GetValue<string>() == "orders-api@1.8.0")!;
        Assert.AreEqual("payments-api@2.3.1", deps["dependsOn"]![0]!.GetValue<string>());
    }

    [TestMethod]
    public void The_change_request_says_what_how_and_how_to_back_out()
    {
        using var t = new TestRepo();
        t.Write("environments/live.yaml", t.Read("environments/staging.yaml").Replace("environment: staging", "environment: live"));
        var repo = t.Load();
        var plan = Planner.Create(repo, "live", new ManifestState(t.Snapshot().Env("uat") with { Components = new Dictionary<string, DesiredComponent>() }),
            t.Metadata(), DateTimeOffset.UnixEpoch.AddYears(56), "abc");
        var cr = Reports.ChangeRequest(plan, repo);

        StringAssert.StartsWith(cr["short_description"]!.GetValue<string>(), "Release 2026.11.3 to live: customers-api 1.2.0");
        StringAssert.Contains(cr["implementation_plan"]!.GetValue<string>(), "PAUSE before payments-api");
        StringAssert.Contains(cr["backout_plan"]!.GetValue<string>(), "newly deployed");
        Assert.HasCount(6, cr["cmdb_ci"]!.AsArray());
        StringAssert.Contains(cr["u_owners"]!.GetValue<string>(), "[Platform]\\Payments Team");
    }
}

[TestClass]
public sealed class CliTests
{
    [TestMethod]
    public void Validate_passes_on_the_example_repo()
    {
        using var t = new TestRepo();
        Assert.AreEqual(Cli.Ok, Cli.Run(["validate", "--repo", t.Repo, "--local-feed", t.Feed]));
    }

    [TestMethod]
    public void Bad_arguments_exit_2_with_a_useful_message()
    {
        Assert.AreEqual(Cli.Usage, Cli.Run(["deploy"]));
        Assert.AreEqual(Cli.Usage, Cli.Run(["plan", "--environment", "live"]));
    }

    [TestMethod]
    public void Plan_then_apply_in_waves_against_a_state_file()
    {
        using var t = new TestRepo();
        var state = Path.Combine(t.Root, "state.json");
        File.WriteAllText(state, """{ "dev": { "customers-api": "1.2.0", "notifications-func": "3.2.1", "orders-api": "1.8.2", "orders-func": "1.5.0", "payments-api": "2.4.0", "portal-web": "5.2.0" } }""");
        var outDir = Path.Combine(t.Root, "plan");
        string[] common = ["--repo", t.Repo, "--local-feed", t.Feed, "--state-file", state];

        Assert.AreEqual(Cli.Ok, Cli.Run(["plan", "--env", "dev", "--out", outDir, .. common]));
        var plan = Plan.FromJson(File.ReadAllText(Path.Combine(outDir, "plan.json")));
        Assert.AreEqual("customers-api", plan.Changes.Single().Component);

        Assert.AreEqual(Cli.Drift, Cli.Run(["diff", "--env", "dev", "--fail-on-drift", .. common]));

        var azure = new FakeAzure();
        var ctx = new CliContext { Runner = azure, Http = new HttpClient(new FakeHttp(azure)), Apply = new ApplyOptions { ProbeAttempts = 1, ProbeDelay = TimeSpan.Zero } };
        azure.Running["app-customers-dev.test"] = "1.2.0";
        azure.Running["app-customers-dev-staging.test"] = "1.2.0";
        Assert.AreEqual(Cli.Ok, Cli.Run(["apply", "--plan", Path.Combine(outDir, "plan.json"), "--wave", "1", .. common], ctx));
        Assert.IsTrue(azure.Mutations.Any(m => m.StartsWith("webapp deploy", StringComparison.Ordinal) && m.Contains("app-customers-dev")));

        Assert.AreEqual(Cli.Ok, Cli.Run(["diff", "--env", "dev", "--fail-on-drift", .. common]), "the state file now says 1.3.0");
        Assert.AreEqual("1.3.0", JsonNode.Parse(File.ReadAllText(state))!["dev"]!["customers-api"]!.GetValue<string>());
    }

    [TestMethod]
    public void Apply_needs_exactly_one_part()
    {
        using var t = new TestRepo();
        var outDir = Path.Combine(t.Root, "plan");
        var state = Path.Combine(t.Root, "state.json");
        Cli.Run(["plan", "--env", "dev", "--out", outDir, "--repo", t.Repo, "--local-feed", t.Feed, "--state-file", state]);
        Assert.AreEqual(Cli.Failed, Cli.Run(["apply", "--plan", Path.Combine(outDir, "plan.json"), "--wave", "1", "--post-sync", "--repo", t.Repo, "--local-feed", t.Feed, "--state-file", state]));
    }

    [TestMethod]
    public void Bump_is_what_a_build_pipeline_runs_to_move_dev()
    {
        using var t = new TestRepo();
        t.AddPackage("orders-api", "1.8.3", "requires:\n  - component: payments-api\n    minVersion: 2.3.0\n    reason: x\n");
        Assert.AreEqual(Cli.Ok, Cli.Run(["bump", "--env", "dev", "--component", "orders-api", "--version", "1.8.3", "--repo", t.Repo]));
        StringAssert.Contains(t.Read("environments/dev.yaml"), "  orders-api: 1.8.3\n");
        t.Validate().IsClean();
    }
}
