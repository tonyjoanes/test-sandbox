using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Inventory.Api.Tests;

[TestClass]
public sealed class ItemEndpointTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Version_DefaultsToDev()
    {
        var body = await _client.GetFromJsonAsync<VersionResponse>("/version");

        Assert.AreEqual("dev", body?.Version);
    }

    [TestMethod]
    public async Task Post_ThenGet_RoundTripsTheItem()
    {
        var created = await _client.PostAsJsonAsync("/items", new NewItem("Widget", 3));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var item = await created.Content.ReadFromJsonAsync<Item>();

        var fetched = await _client.GetFromJsonAsync<Item>($"/items/{item!.Id}");

        Assert.AreEqual(new Item(item.Id, "Widget", 3), fetched);
    }

    [TestMethod]
    public async Task Get_UnknownId_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/items/999");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    [DataRow("", 1)]
    [DataRow("Widget", -1)]
    public async Task Post_InvalidItem_ReturnsValidationProblem(string name, int quantity)
    {
        var response = await _client.PostAsJsonAsync("/items", new NewItem(name, quantity));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record VersionResponse(string Version);
}
