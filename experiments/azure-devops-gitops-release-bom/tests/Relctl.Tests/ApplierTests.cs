namespace Relctl.Tests;

/// <summary>Deploying, against a fake Azure that tracks what runs in each slot.</summary>
[TestClass]
public sealed class ApplierTests
{
    private sealed class Release : IDisposable
    {
        public TestRepo Repo { get; } = new();
        public FakeAzure Azure { get; } = new();
        public ReleaseRepo Loaded { get; private set; } = null!;
        public Plan Plan { get; private set; } = null!;

        /// <summary>
        /// Live is on 2026.11.2 and its manifest says 2026.11.3 (what's in staging). Any other
        /// environment starts exactly as its manifest says; `setup` then changes the manifest.
        /// </summary>
        public Release(string env = "live", Action<TestRepo>? setup = null)
        {
            var deployed = env == "live"
                ? new Dictionary<string, string> { ["customers-api"] = "1.1.4", ["notifications-func"] = "3.1.0", ["orders-api"] = "1.7.3", ["orders-func"] = "1.3.0", ["payments-api"] = "2.2.7", ["portal-web"] = "5.0.2" }
                : Repo.Load().Env(env).Components.ToDictionary(c => c.Key, c => c.Value.Version.ToString());
            if (env == "live")
                Repo.Write("environments/live.yaml", Repo.Read("environments/staging.yaml").Replace("environment: staging", "environment: live"));
            setup?.Invoke(Repo);
            Loaded = Repo.Load();
            foreach (var (name, version) in deployed) Azure.SetDeployed(Loaded.Catalog[name], env, version);
        }

        public Release Planned(string env = "live")
        {
            Plan = Planner.Create(Loaded, env, new AzureTagState(new Az(Azure)), Repo.Metadata(), new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero), null);
            return this;
        }

        public Applier Applier(bool dryRun = false)
        {
            ICommandRunner runner = dryRun ? new DryRunRunner(Azure) : Azure;
            return new Applier(Loaded, new LocalFeed(Repo.Feed), new AzureTagState(new Az(runner)), new Az(runner), runner,
                new HttpClient(new FakeHttp(Azure)),
                new ApplyOptions { DryRun = dryRun, ProbeAttempts = 3, ProbeDelay = TimeSpan.Zero, WorkDir = Path.Combine(Repo.Root, "work") });
        }

        public string Tag(string component, string env = "live")
        {
            var c = Loaded.Catalog[component];
            return Azure.Tags[c.Targets[env].ResourceId(c.Type)].GetValueOrDefault(AzureTagState.VersionTag(component)) ?? "(none)";
        }

        public void Dispose() => Repo.Dispose();
    }

    [TestMethod]
    public void An_api_goes_to_the_slot_is_checked_then_swapped_in_and_tagged()
    {
        using var r = new Release().Planned();
        r.Applier().ApplyWave(r.Plan, 1);

        CollectionAssert.AreEqual(new[]
        {
            "webapp deploy --subscription 00000000-0000-0000-0000-00000000b002 --resource-group rg-customers-prd --name app-customers-prd --src-path " +
                Path.Combine(r.Repo.Feed, "customers-api", "1.2.0", "app.zip") + " --type zip --slot staging",
            "webapp deployment slot swap --subscription 00000000-0000-0000-0000-00000000b002 --resource-group rg-customers-prd --name app-customers-prd --slot staging --target-slot production",
            "tag update --resource-id /subscriptions/00000000-0000-0000-0000-00000000b002/resourceGroups/rg-customers-prd/providers/Microsoft.Web/sites/app-customers-prd --operation Merge --tags relctl.customers-api.version=1.2.0 relctl.customers-api.release=2026.11.3",
        }, r.Azure.Mutations.ToArray());
        Assert.AreEqual("1.2.0", r.Azure.Running["app-customers-prd.test"]);
        Assert.AreEqual("1.1.4", r.Azure.Running["app-customers-prd-staging.test"], "the old build waits in the slot, ready to swap back");
        Assert.AreEqual("1.2.0", r.Tag("customers-api"));
    }

    [TestMethod]
    public void If_post_sync_fails_after_the_swap_it_swaps_back()
    {
        using var r = new Release().Planned();
        r.Azure.Unhealthy.Add("app-customers-prd-staging.test"); // the new build will be unhealthy once live

        var e = Assert.Throws<RelctlException>(() => r.Applier().ApplyWave(r.Plan, 1));

        StringAssert.Contains(e.Message, "swapped back to 1.1.4");
        Assert.AreEqual(2, r.Azure.Mutations.Count(m => m.StartsWith("webapp deployment slot swap", StringComparison.Ordinal)));
        Assert.AreEqual("1.1.4", r.Azure.Running["app-customers-prd.test"], "production is back on the old build");
        Assert.AreEqual("1.1.4", r.Tag("customers-api"), "the tag still says what is really running");
    }

    [TestMethod]
    public void If_the_slot_never_reports_the_new_version_nothing_is_swapped()
    {
        using var r = new Release().Planned();
        r.Azure.Override = c => c.Args.Take(2).SequenceEqual(["webapp", "deploy"]) ? new CommandResult(0, "", "") : null; // "succeeds" but nothing changes

        var e = Assert.Throws<RelctlException>(() => r.Applier().ApplyWave(r.Plan, 1));

        StringAssert.Contains(e.Message, "the staging slot never reported 1.2.0");
        Assert.IsFalse(r.Azure.Mutations.Any(m => m.Contains("swap")), "production must not be touched");
    }

    [TestMethod]
    public void A_failed_az_command_fails_the_wave()
    {
        using var r = new Release().Planned();
        r.Azure.Override = c => c.Args.Take(2).SequenceEqual(["webapp", "deploy"]) ? new CommandResult(1, "", "ERROR: Conflict") : null;
        var e = Assert.Throws<RelctlException>(() => r.Applier().ApplyWave(r.Plan, 1));
        StringAssert.Contains(e.Message, "ERROR: Conflict");
    }

    [TestMethod]
    public void Rerunning_a_wave_skips_what_already_went_out()
    {
        using var r = new Release().Planned();
        r.Applier().ApplyWave(r.Plan, 1);
        r.Azure.Calls.Clear();
        r.Applier().ApplyWave(r.Plan, 1);
        Assert.IsEmpty(r.Azure.Mutations);
    }

    [TestMethod]
    public void If_someone_changed_the_environment_since_the_plan_it_stops()
    {
        using var r = new Release().Planned();
        r.Azure.SetDeployed(r.Loaded.Catalog["customers-api"], "live", "1.1.5"); // a hand-deployed hotfix
        var e = Assert.Throws<RelctlException>(() => r.Applier().ApplyWave(r.Plan, 1));
        StringAssert.Contains(e.Message, "is on 1.1.5, but the plan expected 1.1.4");
        Assert.IsEmpty(r.Azure.Mutations);
    }

    [TestMethod]
    public void A_static_site_gets_this_environments_config_json_then_is_checked()
    {
        using var r = new Release().Planned();
        r.Applier().ApplyWave(r.Plan, 4);

        var uploaded = r.Azure.Uploads["stportalprd"];
        Assert.AreEqual("<html>portal</html>", uploaded["index.html"]);
        var config = System.Text.Json.JsonDocument.Parse(uploaded["config.json"]).RootElement;
        Assert.AreEqual("https://api.contoso.example", config.GetProperty("apiBaseUrl").GetString());
        Assert.AreEqual("live", config.GetProperty("environment").GetString());
        Assert.AreEqual("5.1.0", config.GetProperty("version").GetString());
        Assert.AreEqual("5.1.0", r.Tag("portal-web"));
        Assert.IsTrue(r.Azure.Mutations.Any(m => m.StartsWith("functionapp deployment source config-zip", StringComparison.Ordinal) && m.Contains("--slot staging")), "orders-func is in wave 4 too");
    }

    private static Action<TestRepo> DevNotificationsTo(string version) => t =>
    {
        t.AddPackage("notifications-func", version);
        t.Edit("environments/dev.yaml", "notifications-func: 3.2.1", $"notifications-func: {version}");
    };

    [TestMethod]
    public void Without_a_slot_it_deploys_straight_to_production()
    {
        using var r = new Release("dev", DevNotificationsTo("3.2.2")).Planned("dev");
        Assert.HasCount(1, r.Plan.Changes);

        r.Applier().ApplyWave(r.Plan, 1);

        var deploy = r.Azure.Mutations.First();
        StringAssert.StartsWith(deploy, "functionapp deployment source config-zip");
        Assert.DoesNotContain("--slot", deploy);
        Assert.IsFalse(r.Azure.Mutations.Any(m => m.Contains("swap")));
        Assert.AreEqual("3.2.2", r.Tag("notifications-func", "dev"));
    }

    [TestMethod]
    public void Without_a_slot_a_failed_check_says_how_to_roll_back()
    {
        using var r = new Release("dev", DevNotificationsTo("3.2.2")).Planned("dev");
        r.Azure.Override = c => c.Args.Take(4).SequenceEqual(["functionapp", "deployment", "source", "config-zip"]) ? new CommandResult(0, "", "") : null;

        var e = Assert.Throws<RelctlException>(() => r.Applier().ApplyWave(r.Plan, 1));

        StringAssert.Contains(e.Message, "has no slot to swap back from. Roll back with a manifest change: `notifications-func: { version: 3.2.1, rollback:");
    }

    [TestMethod]
    public void A_pre_sync_hook_gets_the_release_details_and_a_failure_stops_the_deploy()
    {
        using var r = new Release().Planned();
        var envDump = Path.Combine(r.Repo.Root, "hook-env.txt");
        r.Repo.Write("hooks/run-migration-bundle.sh", $"env | grep ^RELCTL_ | sort > '{envDump}'\necho 'migration failed: timeout' >&2\nexit 3\n");

        var e = Assert.Throws<RelctlException>(() => r.Applier().ApplyWave(r.Plan, 1));

        StringAssert.Contains(e.Message, "pre-sync hook \"migrate-database\" (hooks/run-migration-bundle.sh) failed with exit code 3. migration failed: timeout");
        var env = File.ReadAllText(envDump);
        StringAssert.Contains(env, "RELCTL_COMPONENT=customers-api");
        StringAssert.Contains(env, "RELCTL_VERSION=1.2.0");
        StringAssert.Contains(env, "RELCTL_PREVIOUS_VERSION=1.1.4");
        StringAssert.Contains(env, "RELCTL_RESOURCE_NAME=app-customers-prd");
        StringAssert.Contains(env, $"RELCTL_PACKAGE_DIR={Path.Combine(r.Repo.Feed, "customers-api", "1.2.0")}");
        Assert.IsEmpty(r.Azure.Mutations, "nothing deploys if pre-sync fails");
    }

    [TestMethod]
    public void Release_hooks_run_once_around_the_whole_release()
    {
        using var r = new Release(setup: t => t.Edit("environments/live.yaml", "pauses:", "hooks:\n  preSync:\n    - name: announce\n      script: hooks/announce-release.sh\npauses:"));
        r.Planned();
        var marker = Path.Combine(r.Repo.Root, "announced.txt");
        r.Repo.Write("hooks/announce-release.sh", $"echo \"$RELCTL_RELEASE $RELCTL_CHANGES\" > '{marker}'\n");
        r.Applier().RunReleaseHooks(r.Plan, preSync: true);
        Assert.AreEqual("2026.11.3 customers-api@1.2.0,payments-api@2.3.1,orders-api@1.8.0,orders-func@1.4.0,portal-web@5.1.0", File.ReadAllText(marker).Trim());
    }

    [TestMethod]
    public void A_dry_run_reads_but_changes_nothing()
    {
        using var r = new Release().Planned();
        for (var w = 1; w <= r.Plan.WaveCount; w++) r.Applier(dryRun: true).ApplyWave(r.Plan, w);
        Assert.IsEmpty(r.Azure.Mutations);
        Assert.IsFalse(r.Azure.Calls.Any(c => c.File == "bash"), "hooks don't run in a dry run");
        Assert.AreEqual("1.1.4", r.Tag("customers-api"));
    }
}
