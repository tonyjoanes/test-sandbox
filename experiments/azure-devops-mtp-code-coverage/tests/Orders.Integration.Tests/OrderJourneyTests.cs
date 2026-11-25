using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Orders.Api;
using Orders.Domain;

namespace Orders.Integration.Tests;

/// <summary>
/// End-to-end journeys through the real HTTP pipeline. Each test runs Orders.Api AND Orders.Domain,
/// which is exactly why this project's coverage is reported separately and gated lower: a journey
/// test "touches" a lot of code while asserting on only a little of it. See docs/how-coverage-works.md.
/// </summary>
public sealed class OrderJourneyTests : IDisposable
{
    private readonly ApiHost _host = new();
    private readonly HttpClient _client;

    public OrderJourneyTests() => _client = _host.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task Customer_creates_places_and_reads_back_an_order()
    {
        var created = await CreateAsync(new CreateOrderRequest(
            "journey-1", CustomerTier.Standard, "GBP", [new OrderLineRequest("BOOK", 4, 15m)], null));

        var placed = await _client.PostAsync($"/orders/{created.Id}/place", null, Ct);
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);

        var fetched = await _client.GetFromJsonAsync<OrderResponse>($"/orders/{created.Id}", Ct);
        Assert.Equal("Placed", fetched!.Status);
        Assert.Equal(60m, fetched.Quote.Total);
    }

    [Fact]
    public async Task Gold_customer_with_coupon_gets_both_discounts()
    {
        var created = await CreateAsync(new CreateOrderRequest(
            "journey-2", CustomerTier.Gold, "GBP", [new OrderLineRequest("DESK", 1, 200m)], "save10"));

        // 200 - 10% tier = 180, - 10 coupon = 170, free shipping.
        Assert.Equal(170m, created.Quote.Total);
        Assert.Equal(["tier:gold", "coupon:SAVE10", "shipping:free"], created.Quote.AppliedRules);
    }

    [Fact]
    public async Task Cancelled_order_cannot_be_placed()
    {
        var created = await CreateAsync(new CreateOrderRequest(
            "journey-3", CustomerTier.Silver, "GBP", [new OrderLineRequest("PEN", 1, 3m)], null));

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/orders/{created.Id}/cancel", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/orders/{created.Id}/place", null, Ct)).StatusCode);
    }

    private async Task<OrderResponse> CreateAsync(CreateOrderRequest request)
    {
        var response = await _client.PostAsJsonAsync("/orders", request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>(Ct))!;
    }

    /// <summary>The real API in memory, clock pinned to a Tuesday so weekend pricing is off.</summary>
    private sealed class ApiHost : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(
                new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero)))));
        }
    }
}
