using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Orders.Domain;

namespace Orders.Api.Tests;

// A fresh API per test (xunit creates a new class instance per test), so tests don't share orders.
public sealed class OrderEndpointTests : IDisposable
{
    private readonly OrdersApiFactory _factory = new();
    private readonly HttpClient _client;

    public OrderEndpointTests() => _client = _factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static CreateOrderRequest ValidRequest(CustomerTier tier = CustomerTier.Standard, string? coupon = null, string customerId = "cust-1") =>
        new(customerId, tier, "GBP", [new OrderLineRequest("BOOK", 2, 12.50m), new OrderLineRequest("PEN", 1, 3m)], coupon);

    private async Task<OrderResponse> CreateAsync(CreateOrderRequest request)
    {
        var response = await _client.PostAsJsonAsync("/orders", request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>(Ct))!;
    }

    [Fact]
    public async Task Health_ReturnsOk() =>
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health", Ct)).StatusCode);

    [Fact]
    public async Task CreateOrder_ReturnsCreatedWithQuote()
    {
        var response = await _client.PostAsJsonAsync("/orders", ValidRequest(), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<OrderResponse>(Ct))!;
        Assert.Equal($"/orders/{order.Id}", response.Headers.Location?.ToString());
        Assert.Equal(32.99m, order.Quote.Total);
    }

    [Fact]
    public async Task CreateOrder_WithCoupon_AppliesIt()
    {
        var order = await CreateAsync(ValidRequest(CustomerTier.Gold, "save10"));

        Assert.Equal("SAVE10", order.CouponCode);
        Assert.Equal(["tier:gold", "coupon:SAVE10"], order.Quote.AppliedRules);
    }

    [Theory]
    [InlineData("", "GBP")]
    [InlineData("cust-1", "gbp")]
    public async Task CreateOrder_InvalidRequest_Returns400(string customerId, string currency)
    {
        var response = await _client.PostAsJsonAsync("/orders", ValidRequest() with { CustomerId = customerId, Currency = currency }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetOrder_ExistingAndUnknown()
    {
        var created = await CreateAsync(ValidRequest());

        Assert.Equal(created.Id, (await _client.GetFromJsonAsync<OrderResponse>($"/orders/{created.Id}", Ct))?.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/orders/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task ListOrders_FiltersByCustomer()
    {
        await CreateAsync(ValidRequest(customerId: "alice"));
        await CreateAsync(ValidRequest(customerId: "bob"));

        var orders = await _client.GetFromJsonAsync<List<OrderResponse>>("/orders?customerId=alice", Ct);

        Assert.Equal("alice", Assert.Single(orders!).CustomerId);
    }

    [Fact]
    public async Task ListOrders_WithoutCustomerId_ReturnsValidationProblem()
    {
        var response = await _client.GetAsync("/orders", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct);
        Assert.True(problem!.Errors.ContainsKey("customerId"));
    }

    [Fact]
    public async Task PlaceThenPlaceAgain_ReturnsConflict()
    {
        var created = await CreateAsync(ValidRequest());

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/orders/{created.Id}/place", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/orders/{created.Id}/place", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Cancel_Draft_MovesToCancelled()
    {
        var created = await CreateAsync(ValidRequest());

        var order = await (await _client.PostAsync($"/orders/{created.Id}/cancel", null, Ct)).Content.ReadFromJsonAsync<OrderResponse>(Ct);

        Assert.Equal("Cancelled", order?.Status);
    }

    [Theory]
    [InlineData("place")]
    [InlineData("cancel")]
    public async Task Transition_UnknownOrder_Returns404(string action) =>
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/orders/{Guid.NewGuid()}/{action}", null, Ct)).StatusCode);
}
