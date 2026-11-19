namespace Relctl.Tests;

[TestClass]
public sealed class SemVerTests
{
    [TestMethod]
    [DataRow("1.2.3")]
    [DataRow("0.0.0")]
    [DataRow("10.20.30")]
    public void Parses_exact_versions(string text) => Assert.AreEqual(text, SemVer.Parse(text).ToString());

    [TestMethod]
    [DataRow("1.2")]
    [DataRow("^1.2.3")]
    [DataRow("1.2.3-beta.1")]
    [DataRow("latest")]
    [DataRow("01.2.3")]
    [DataRow("")]
    public void Rejects_anything_that_isnt_one_exact_build(string text) => Assert.IsFalse(SemVer.TryParse(text, out _));

    [TestMethod]
    public void Compares_numerically_not_alphabetically()
    {
        Assert.IsTrue(SemVer.Parse("1.10.0") > SemVer.Parse("1.9.9"));
        Assert.IsTrue(SemVer.Parse("2.0.0") > SemVer.Parse("1.99.99"));
        Assert.IsTrue(SemVer.Parse("1.2.3") == SemVer.Parse("1.2.3"));
    }
}

[TestClass]
public sealed class LoaderTests
{
    [TestMethod]
    public void The_example_release_repo_is_valid()
    {
        using var t = new TestRepo();
        t.Validate().IsClean();
        var repo = t.Load();
        Assert.HasCount(6, repo.Catalog);
        Assert.HasCount(4, repo.Environments);
        Assert.AreEqual(ComponentType.StaticSite, repo.Catalog["portal-web"].Type);
        Assert.AreEqual("https://api.contoso.example", repo.Catalog["portal-web"].Targets["live"].Config["apiBaseUrl"]);
        Assert.IsNull(repo.Catalog["notifications-func"].Targets["dev"].Slot);
    }

    [TestMethod]
    public void A_typo_in_a_key_is_an_error_with_its_line_not_silently_ignored()
    {
        using var t = new TestRepo();
        t.Edit("components/orders-api.yaml", "versionPath:", "versionpath:");
        var d = t.Validate();
        d.HasError("unknown key `versionpath`");
        var error = d.Errors.First();
        Assert.AreEqual("components/orders-api.yaml", error.File);
        Assert.IsGreaterThan(0, error.Line);
        StringAssert.Contains(error.Message, "versionPath", "the message should list the allowed keys");
    }

    [TestMethod]
    public void A_typo_in_an_environment_manifest_is_an_error()
    {
        using var t = new TestRepo();
        t.Edit("environments/staging.yaml", "pauses:", "pause:");
        t.Validate().HasError("unknown key `pause`");
    }

    [TestMethod]
    [DataRow("^1.2.0")]
    [DataRow("latest")]
    [DataRow("1.2")]
    public void A_manifest_version_must_be_exact(string version)
    {
        using var t = new TestRepo();
        t.Edit("environments/uat.yaml", "customers-api: 1.2.0", $"customers-api: \"{version}\"");
        t.Validate().HasError("isn't an exact x.y.z version");
    }

    [TestMethod]
    public void A_rollback_entry_needs_a_reason()
    {
        using var t = new TestRepo();
        t.Edit("environments/uat.yaml", "customers-api: 1.2.0", "customers-api: { version: 1.2.0 }");
        t.Validate().HasError("missing `rollback`");
    }

    [TestMethod]
    public void Subscription_must_be_an_id_because_resource_ids_need_it()
    {
        using var t = new TestRepo();
        t.Edit("components/payments-api.yaml", "subscription: 00000000-0000-0000-0000-00000000a001", "subscription: Contoso Non-Prod");
        t.Validate().HasError("should be the subscription ID");
    }

    [TestMethod]
    public void File_names_must_match_what_they_describe()
    {
        using var t = new TestRepo();
        t.Edit("environments/uat.yaml", "environment: uat", "environment: staging");
        t.Validate().HasError("the file is named uat.yaml");
    }

    [TestMethod]
    public void A_hook_is_a_script_or_an_http_check_never_both()
    {
        using var t = new TestRepo();
        t.Edit("components/payments-api.yaml", "      http: /health/ready", "      http: /health/ready\n      script: hooks/announce-release.sh");
        t.Validate().HasError("needs exactly one of `script` or `http`");
    }

    [TestMethod]
    public void An_unknown_component_type_is_an_error()
    {
        using var t = new TestRepo();
        t.Edit("components/orders-func.yaml", "type: function-app", "type: aks");
        t.Validate().HasError("isn't one relctl can deploy");
    }

    [TestMethod]
    public void The_promotion_chain_cant_go_in_a_circle()
    {
        using var t = new TestRepo();
        t.Edit("policy.yaml", "  - name: dev\n", "  - name: dev\n    promotesFrom: live\n");
        t.Validate().HasError("round in a circle");
    }
}
