using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Orders.Api.Tests;

/// <summary>Hosts the real API in memory, with the clock pinned so pricing is deterministic.</summary>
public sealed class OrdersApiFactory : WebApplicationFactory<Program>
{
    // A Tuesday, so the WEEKEND15 coupon is off unless a test moves the clock.
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
    }
}
