namespace Relctl.Tests;

/// <summary>Rules that only need the repo at one commit.</summary>
[TestClass]
public sealed class ConsistencyRuleTests
{
    [TestMethod]
    public void A_version_must_exist_in_the_feed()
    {
        using var t = new TestRepo();
        t.Edit("environments/dev.yaml", "orders-api: 1.8.2", "orders-api: 1.8.3");
        t.Validate().HasError("orders-api 1.8.3 doesn't exist in the local feed");
    }

    [TestMethod]
    public void A_component_must_be_in_the_catalog()
    {
        using var t = new TestRepo();
        t.Edit("environments/dev.yaml", "components:\n", "components:\n  invoices-api: 1.0.0\n");
        t.Validate().HasError("\"invoices-api\" isn't in the component catalog");
    }

    [TestMethod]
    public void A_component_needs_a_target_for_every_environment_it_is_in()
    {
        using var t = new TestRepo();
        var text = t.Read("components/orders-func.yaml");
        t.Write("components/orders-func.yaml", text[..text.IndexOf("  live:", StringComparison.Ordinal)]);
        t.Validate().HasError("\"orders-func\" has no `live` target");
    }

    [TestMethod]
    public void A_dependency_must_be_new_enough_in_the_same_environment()
    {
        using var t = new TestRepo();
        // payments-api 2.3.1 says (inside its package) it needs customers-api >= 1.2.0.
        t.Edit("environments/uat.yaml", "customers-api: 1.2.0", "customers-api: 1.1.4");
        t.Validate().HasError("payments-api 2.3.1 needs customers-api >= 1.2.0, but uat pins customers-api 1.1.4. Why: Calls GET /customers/{id}/billing-profile");
    }

    [TestMethod]
    public void A_dependency_must_be_present_at_all()
    {
        using var t = new TestRepo();
        t.Edit("environments/uat.yaml", "  customers-api: 1.2.0\n", "");
        t.Validate().HasError("needs customers-api >= 1.2.0, but customers-api isn't in uat");
    }

    [TestMethod]
    public void Rules_come_from_the_exact_version_being_released()
    {
        using var t = new TestRepo();
        // payments-api 2.5.0 dropped its need for customers-api 1.2.0, so an older customers-api is fine with it.
        t.AddPackage("payments-api", "2.5.0");
        t.Edit("environments/dev.yaml", "payments-api: 2.4.0", "payments-api: 2.5.0");
        t.Edit("environments/dev.yaml", "customers-api: 1.3.0", "customers-api: 1.1.4");
        t.Validate().IsClean();
    }

    [TestMethod]
    public void Requirements_that_go_round_in_a_circle_are_an_error()
    {
        using var t = new TestRepo();
        t.AddPackage("customers-api", "1.4.0", "requires:\n  - component: payments-api\n    minVersion: 2.0.0\n    reason: test\n");
        t.Edit("environments/dev.yaml", "customers-api: 1.3.0", "customers-api: 1.4.0");
        t.Validate().HasError("go round in a circle");
    }

    [TestMethod]
    public void A_pause_belongs_to_one_release_and_goes_stale_after()
    {
        using var t = new TestRepo();
        t.Edit("environments/staging.yaml", "environment: staging\nrelease: 2026.11.3", "environment: staging\nrelease: 2026.11.4");
        t.Validate().HasError("this pause is for release 2026.11.3, but this is release 2026.11.4. Delete it");
    }

    [TestMethod]
    public void A_pause_must_be_before_a_real_component()
    {
        using var t = new TestRepo();
        t.Edit("environments/staging.yaml", "before: payments-api", "before: payment-api");
        t.Validate().HasError("pause before \"payment-api\", which isn't a component");
    }

    [TestMethod]
    public void Every_environment_in_the_policy_needs_a_manifest()
    {
        using var t = new TestRepo();
        File.Delete(Path.Combine(t.Repo, "environments/uat.yaml"));
        t.Validate().HasError("is missing. policy.yaml lists \"uat\"");
    }

    [TestMethod]
    public void Environments_after_the_first_need_a_release_name()
    {
        using var t = new TestRepo();
        t.Edit("environments/uat.yaml", "release: 2026.11.4\n", "");
        t.Validate().HasError("needs a `release:` name");
    }

    [TestMethod]
    public void A_hook_script_must_exist()
    {
        using var t = new TestRepo();
        File.Delete(Path.Combine(t.Repo, "hooks/announce-release.sh"));
        t.Validate().HasError("runs hooks/announce-release.sh, which doesn't exist");
    }
}

/// <summary>Rules about what a PR changes, compared with the branch it merges into.</summary>
[TestClass]
public sealed class ChangeRuleTests
{
    private static void Promote(TestRepo t, string from, string to, string? only = null)
    {
        var repo = t.Load();
        var (text, _, _) = ManifestEditor.Promote(repo, from, to, t.Read($"environments/{to}.yaml"), only?.Split(','), null);
        t.Write($"environments/{to}.yaml", text);
    }

    [TestMethod]
    public void Promoting_exactly_what_is_upstream_passes()
    {
        using var t = new TestRepo();
        var main = t.Snapshot();
        Promote(t, "staging", "live");
        t.Validate(main).IsClean();
    }

    [TestMethod]
    public void A_version_cant_skip_an_environment()
    {
        using var t = new TestRepo();
        var main = t.Snapshot();
        t.Edit("environments/live.yaml", "release: 2026.11.2", "release: 2026.11.3");
        t.Edit("environments/live.yaml", "notifications-func: 3.1.0", "notifications-func: 3.2.0"); // only in uat so far
        t.Validate(main).HasError("notifications-func 3.2.0 must be in staging before it can go to live, but staging has notifications-func 3.1.0");
    }

    [TestMethod]
    public void A_version_going_backwards_must_say_it_is_a_rollback()
    {
        using var t = new TestRepo();
        var main = t.Snapshot();
        t.Edit("environments/uat.yaml", "release: 2026.11.4", "release: 2026.11.5");
        t.Edit("environments/uat.yaml", "notifications-func: 3.2.0", "notifications-func: 3.1.0");
        t.Validate(main).HasError("goes backwards from 3.2.0 to 3.1.0. If that's a rollback, say so");
    }

    [TestMethod]
    public void A_rollback_skips_promotion_order_because_live_cant_wait_for_uat()
    {
        using var t = new TestRepo();
        var main = t.Snapshot();
        t.Edit("environments/uat.yaml", "release: 2026.11.4", "release: 2026.11.5");
        t.Edit("environments/uat.yaml", "notifications-func: 3.2.0", "notifications-func: { version: 3.1.0, rollback: \"INC1 duplicate emails\" }");
        t.Validate(main).IsClean();
    }

    [TestMethod]
    public void A_rollback_must_actually_go_backwards()
    {
        using var t = new TestRepo();
        var main = t.Snapshot();
        t.Edit("environments/uat.yaml", "release: 2026.11.4", "release: 2026.11.5");
        t.Edit("environments/uat.yaml", "notifications-func: 3.2.0", "notifications-func: { version: 3.2.1, rollback: \"sneaky\" }");
        t.Validate(main).HasError("is marked as a rollback, but 3.2.1 isn't older than the current 3.2.0");
    }

    [TestMethod]
    public void Changing_versions_needs_a_new_release_name()
    {
        using var t = new TestRepo();
        var main = t.Snapshot();
        t.Edit("environments/live.yaml", "notifications-func: 3.1.0", "notifications-func: { version: 3.0.9, rollback: \"x\" }");
        t.AddPackage("notifications-func", "3.0.9");
        t.Validate(main).HasError("versions changed but `release` is still 2026.11.2");
    }

    [TestMethod]
    public void Nothing_can_change_during_a_freeze()
    {
        using var t = new TestRepo();
        var main = t.Snapshot();
        Promote(t, "staging", "live");
        t.Validate(main, today: new DateOnly(2026, 12, 24)).HasError("live is frozen from 2026-12-18 to 2027-01-04 (Christmas trading freeze)");
        t.Validate(main, today: new DateOnly(2027, 1, 5)).IsClean();
    }

    [TestMethod]
    public void A_freeze_doesnt_block_PRs_that_dont_change_versions()
    {
        using var t = new TestRepo();
        var main = t.Snapshot();
        t.Edit("environments/live.yaml", "environment: live", "# Christmas cover rota: see the wiki\nenvironment: live");
        t.Validate(main, today: new DateOnly(2026, 12, 24)).IsClean();
    }

    [TestMethod]
    public void Removing_a_component_warns_that_it_stays_deployed()
    {
        using var t = new TestRepo();
        var main = t.Snapshot();
        t.Edit("environments/dev.yaml", "  notifications-func: 3.2.1\n", "");
        var d = t.Validate(main);
        d.IsClean();
        d.HasWarning("\"notifications-func\" was removed. relctl never deletes anything");
    }

    [TestMethod]
    public void Base_is_read_from_git_without_checking_it_out()
    {
        using var t = new TestRepo();
        void Git(params string[] args)
        {
            var r = ProcessRunner.Instance.Run(new Command("git", ["-c", "user.name=t", "-c", "user.email=t@example.invalid", .. args]) { WorkingDirectory = t.Repo, Quiet = true });
            Assert.AreEqual(0, r.ExitCode, r.StdErr);
        }
        Git("init", "--quiet", "--initial-branch", "main");
        Git("add", ".");
        Git("commit", "--quiet", "-m", "base");
        t.Edit("environments/live.yaml", "release: 2026.11.2", "release: 2026.11.3");
        t.Edit("environments/live.yaml", "orders-api: 1.7.3", "orders-api: 1.8.2");

        var exit = Cli.Run(["validate", "--repo", t.Repo, "--local-feed", t.Feed, "--base", "main", "--today", "2026-11-19"]);
        Assert.AreEqual(Cli.Failed, exit);

        var main = Loader.Load(new GitRefSource(t.Repo, "main"), new Diagnostics(), t.Repo);
        Assert.AreEqual(SemVer.Parse("1.7.3"), main.Env("live").Components["orders-api"].Version);
    }
}
