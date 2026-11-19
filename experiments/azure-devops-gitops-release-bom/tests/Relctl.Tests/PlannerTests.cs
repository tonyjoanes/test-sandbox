namespace Relctl.Tests;

[TestClass]
public sealed class PlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Plan `env` assuming what's deployed is what `deployed` says (component → version).</summary>
    private static Plan PlanFor(TestRepo t, string env, Dictionary<string, string> deployed, DateTimeOffset? now = null)
    {
        var repo = t.Load();
        var state = Path.Combine(t.Root, "state.json");
        File.WriteAllText(state, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { [env] = deployed }));
        return Planner.Create(repo, env, new FileState(state), t.Metadata(), now ?? Now, "abc1234");
    }

    private static readonly Dictionary<string, string> LiveToday = new()
    {
        ["customers-api"] = "1.1.4", ["notifications-func"] = "3.1.0", ["orders-api"] = "1.7.3",
        ["orders-func"] = "1.3.0", ["payments-api"] = "2.2.7", ["portal-web"] = "5.0.2",
    };

    private static string Waves(Plan p) => string.Join(" | ", Enumerable.Range(1, p.WaveCount).Select(w => string.Join(",", p.InWave(w).Select(c => c.Component))));

    [TestMethod]
    public void Waves_follow_what_each_new_version_needs()
    {
        using var t = new TestRepo();
        t.Write("environments/live.yaml", t.Read("environments/staging.yaml").Replace("environment: staging", "environment: live"));
        var plan = PlanFor(t, "live", LiveToday);
        Assert.AreEqual("customers-api | payments-api | orders-api | orders-func,portal-web", Waves(plan));
        Assert.Contains("notifications-func 3.1.0", plan.Unchanged);
        Assert.IsTrue(plan.ChangeRequest, "live needs a change request");
    }

    [TestMethod]
    public void Only_the_difference_from_what_is_deployed_is_planned()
    {
        using var t = new TestRepo();
        var deployed = new Dictionary<string, string>(LiveToday) { ["orders-func"] = "1.3.0" };
        t.Edit("environments/live.yaml", "release: 2026.11.2", "release: 2026.11.3");
        t.Edit("environments/live.yaml", "orders-func: 1.3.0", "orders-func: { version: 1.2.9, rollback: \"x\" }");
        t.AddPackage("orders-func", "1.2.9");
        var plan = PlanFor(t, "live", deployed);
        Assert.HasCount(1, plan.Changes);
        Assert.AreEqual("orders-func", plan.Changes[0].Component);
        Assert.AreEqual("1.3.0", plan.Changes[0].From);
        Assert.AreEqual(1, plan.WaveCount);
    }

    [TestMethod]
    public void Something_already_in_place_doesnt_hold_anything_up()
    {
        using var t = new TestRepo();
        // orders-api is already on 1.8.0, so orders-func 1.4.0 (which needs it) can go straight away.
        var deployed = new Dictionary<string, string>(LiveToday) { ["customers-api"] = "1.2.0", ["payments-api"] = "2.3.1", ["orders-api"] = "1.8.0" };
        t.Write("environments/live.yaml", t.Read("environments/staging.yaml").Replace("environment: staging", "environment: live"));
        var plan = PlanFor(t, "live", deployed);
        Assert.AreEqual("orders-func,portal-web", Waves(plan));
    }

    [TestMethod]
    public void A_rollback_moves_the_callers_off_before_downgrading_what_they_call()
    {
        using var t = new TestRepo();
        var deployed = new Dictionary<string, string>(LiveToday) { ["customers-api"] = "1.2.0", ["payments-api"] = "2.3.1", ["orders-api"] = "1.8.0", ["orders-func"] = "1.4.0", ["portal-web"] = "5.1.0" };
        t.Edit("environments/live.yaml", "release: 2026.11.2", "release: 2026.11.3-rb1");
        // live.yaml already pins the older versions; that's the rollback.
        var plan = PlanFor(t, "live", deployed);
        // orders-func 1.4.0 and portal-web 5.1.0 need orders-api 1.8.0, so they must move off it first.
        // The same for orders-api 1.8.0 → payments 2.3.1, and payments 2.3.1 → customers-api 1.2.0.
        Assert.AreEqual("orders-func,portal-web | orders-api | payments-api | customers-api", Waves(plan));
    }

    [TestMethod]
    public void A_pause_holds_the_wave_of_the_component_it_comes_before()
    {
        using var t = new TestRepo();
        t.Write("environments/live.yaml", t.Read("environments/staging.yaml").Replace("environment: staging", "environment: live"));
        var plan = PlanFor(t, "live", LiveToday);
        Assert.HasCount(1, plan.Pauses);
        Assert.AreEqual(2, plan.Pauses[0].Wave);
        StringAssert.Contains(plan.Pauses[0].Instructions, "customers-billing-backfill");
    }

    [TestMethod]
    public void A_pause_before_something_that_isnt_changing_is_skipped()
    {
        using var t = new TestRepo();
        t.Write("environments/live.yaml", t.Read("environments/staging.yaml").Replace("environment: staging", "environment: live"));
        var deployed = new Dictionary<string, string>(LiveToday) { ["payments-api"] = "2.3.1", ["customers-api"] = "1.2.0" };
        var plan = PlanFor(t, "live", deployed);
        Assert.IsEmpty(plan.Pauses);
        Assert.IsTrue(plan.Notes.Any(n => n.Contains("payments-api isn't changing")));
    }

    [TestMethod]
    public void More_waves_than_the_pipeline_has_is_an_error()
    {
        using var t = new TestRepo();
        t.Edit("policy.yaml", "maxWaves: 5", "maxWaves: 3");
        t.Write("environments/live.yaml", t.Read("environments/staging.yaml").Replace("environment: staging", "environment: live"));
        var e = Assert.Throws<RelctlException>(() => PlanFor(t, "live", LiveToday));
        StringAssert.Contains(e.Message, "needs 4 waves, but the pipeline has 3");
    }

    [TestMethod]
    public void A_freeze_is_enforced_again_at_deploy_time()
    {
        using var t = new TestRepo();
        t.Write("environments/live.yaml", t.Read("environments/staging.yaml").Replace("environment: staging", "environment: live"));
        var e = Assert.Throws<RelctlException>(() => PlanFor(t, "live", LiveToday, new DateTimeOffset(2026, 12, 20, 9, 0, 0, TimeSpan.Zero)));
        StringAssert.Contains(e.Message, "frozen");
    }

    [TestMethod]
    public void No_changes_means_no_hooks_and_no_change_request()
    {
        using var t = new TestRepo();
        var plan = PlanFor(t, "live", LiveToday);
        Assert.IsFalse(plan.HasChanges);
        Assert.IsFalse(plan.ChangeRequest);
        Assert.IsEmpty(plan.PreSync);
        StringAssert.Contains(Reports.PlanMarkdown(plan), "already matches the manifest");
    }

    [TestMethod]
    public void Pipeline_variables_switch_on_exactly_the_waves_and_pauses_needed()
    {
        using var t = new TestRepo();
        t.Write("environments/live.yaml", t.Read("environments/staging.yaml").Replace("environment: staging", "environment: live"));
        var vars = Reports.PipelineVariables(PlanFor(t, "live", LiveToday), 5).ToDictionary(v => v.Name, v => v.Value);
        Assert.AreEqual("true", vars["wave4"]);
        Assert.AreEqual("false", vars["wave5"]);
        Assert.AreEqual("false", vars["pause1"]);
        Assert.AreEqual("true", vars["pause2"]);
        StringAssert.StartsWith(vars["pause2Instructions"], "Before payments-api: Run the");
        Assert.AreEqual("true", vars["changeRequest"]);
    }

    [TestMethod]
    public void A_plan_survives_being_written_and_read_back()
    {
        using var t = new TestRepo();
        t.Write("environments/live.yaml", t.Read("environments/staging.yaml").Replace("environment: staging", "environment: live"));
        var plan = PlanFor(t, "live", LiveToday);
        var again = Plan.FromJson(plan.ToJson());
        Assert.AreEqual(plan.ToJson(), again.ToJson());
        Assert.HasCount(5, again.Changes);
    }
}
