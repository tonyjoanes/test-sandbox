using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Orders.Domain;

namespace Orders.Api.Tests;

[TestClass]
public sealed class OrderEndpointTests
{
    private OrdersApiFactory _factory = null!;
    private HttpClient _client = null!;

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestInitialize]
    public void Start()
    {
        _factory = new OrdersApiFactory();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Stop()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static CreateOrderRequest ValidRequest(
        CustomerTier tier = CustomerTier.Standard,
        string? coupon = null,
        string customerId = "cust-1") =>
        new(customerId, tier, "GBP", [new OrderLineRequest("BOOK", 2, 12.50m), new OrderLineRequest("PEN", 1, 3m)], coupon);

    private async Task<OrderResponse> CreateAsync(CreateOrderRequest request)
    {
        var response = await _client.PostAsJsonAsync("/orders", request, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>(Ct))!;
    }

    [TestMethod]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health", Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task CreateOrder_ReturnsCreatedWithQuoteAndLocation()
    {
        var response = await _client.PostAsJsonAsync("/orders", ValidRequest(), Ct);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<OrderResponse>(Ct))!;
        Assert.AreEqual($"/orders/{order.Id}", response.Headers.Location?.ToString());
        Assert.AreEqual("Draft", order.Status);
        Assert.AreEqual(_factory.Clock.GetUtcNow(), order.CreatedAt);
        Assert.HasCount(2, order.Lines);
        Assert.AreEqual(28m, order.Quote.Subtotal);
        Assert.AreEqual(32.99m, order.Quote.Total);
    }

    [TestMethod]
    public async Task CreateOrder_GoldWithCoupon_AppliesBothDiscounts()
    {
        var order = await CreateAsync(ValidRequest(CustomerTier.Gold, "save10"));

        Assert.AreEqual("SAVE10", order.CouponCode);
        CollectionAssert.AreEqual(new[] { "tier:gold", "coupon:SAVE10" }, order.Quote.AppliedRules.ToArray());
    }

    [TestMethod]
    public async Task CreateOrder_Weekend15_DependsOnTheClock()
    {
        _factory.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero)); // Saturday

        var order = await CreateAsync(ValidRequest(coupon: "WEEKEND15"));

        Assert.Contains("coupon:WEEKEND15", order.Quote.AppliedRules);
    }

    [TestMethod]
    [DataRow("", "GBP", "CustomerId")]
    [DataRow("cust-1", "gbp", "Currency")]
    [DataRow("cust-1", "POUNDS", "Currency")]
    public async Task CreateOrder_InvalidRequest_ReturnsValidationProblem(string customerId, string currency, string field)
    {
        var request = ValidRequest() with { CustomerId = customerId, Currency = currency };

        var response = await _client.PostAsJsonAsync("/orders", request, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct);
        Assert.IsNotNull(problem);
        Assert.IsTrue(problem.Errors.Keys.Any(k => k.Contains(field, StringComparison.OrdinalIgnoreCase)),
            $"Expected an error for {field}, got: {string.Join(", ", problem.Errors.Keys)}");
    }

    [TestMethod]
    public async Task CreateOrder_InvalidLine_ReturnsValidationProblem()
    {
        var request = ValidRequest() with { Lines = [new OrderLineRequest("BOOK", 0, 12.50m)] };

        var response = await _client.PostAsJsonAsync("/orders", request, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task CreateOrder_NoLines_ReturnsValidationProblem()
    {
        var response = await _client.PostAsJsonAsync("/orders", ValidRequest() with { Lines = [] }, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task GetOrder_Existing_ReturnsIt()
    {
        var created = await CreateAsync(ValidRequest());

        var order = await _client.GetFromJsonAsync<OrderResponse>($"/orders/{created.Id}", Ct);

        Assert.AreEqual(created.Id, order?.Id);
    }

    [TestMethod]
    public async Task GetOrder_Unknown_Returns404()
    {
        var response = await _client.GetAsync($"/orders/{Guid.NewGuid()}", Ct);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ListOrders_ReturnsOnlyThatCustomersOrders()
    {
        await CreateAsync(ValidRequest(customerId: "alice"));
        await CreateAsync(ValidRequest(customerId: "alice"));
        await CreateAsync(ValidRequest(customerId: "bob"));

        var orders = await _client.GetFromJsonAsync<List<OrderResponse>>("/orders?customerId=alice", Ct);

        Assert.IsNotNull(orders);
        Assert.HasCount(2, orders);
        Assert.IsTrue(orders.All(o => o.CustomerId == "alice"));
    }

    [TestMethod]
    public async Task ListOrders_WithoutCustomerId_ReturnsValidationProblem()
    {
        var response = await _client.GetAsync("/orders", Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct);
        Assert.IsTrue(problem!.Errors.ContainsKey("customerId"));
    }

    [TestMethod]
    public async Task PlaceOrder_Draft_MovesToPlaced()
    {
        var created = await CreateAsync(ValidRequest());

        var response = await _client.PostAsync($"/orders/{created.Id}/place", null, Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(Ct);
        Assert.AreEqual("Placed", order?.Status);
    }

    [TestMethod]
    public async Task PlaceOrder_Twice_ReturnsConflict()
    {
        var created = await CreateAsync(ValidRequest());
        await _client.PostAsync($"/orders/{created.Id}/place", null, Ct);

        var response = await _client.PostAsync($"/orders/{created.Id}/place", null, Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    public async Task CancelOrder_Draft_MovesToCancelled()
    {
        var created = await CreateAsync(ValidRequest());

        var response = await _client.PostAsync($"/orders/{created.Id}/cancel", null, Ct);

        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(Ct);
        Assert.AreEqual("Cancelled", order?.Status);
    }

    [TestMethod]
    [DataRow("place")]
    [DataRow("cancel")]
    public async Task Transition_UnknownOrder_Returns404(string action)
    {
        var response = await _client.PostAsync($"/orders/{Guid.NewGuid()}/{action}", null, Ct);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
