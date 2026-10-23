using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using AuthPatterns.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthPatterns.Api.Tests;

/// Stands in for the APIM managed-identity token. A request is "from APIM" when X-Test-Caller is set.
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
    : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var caller = Request.Headers["X-Test-Caller"].ToString();
        if (string.IsNullOrEmpty(caller)) return Task.FromResult(AuthenticateResult.NoResult());
        var id = new ClaimsIdentity([new Claim("azp", caller)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name)));
    }
}

public class EndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Secret = "test-primary";
    private readonly HttpClient _client;

    public EndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("BackendAuth:ApimPrincipalId", "apim-mi");
            b.UseSetting("Webhook:PrimarySecret", Secret);
            b.ConfigureServices(s => s.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                    o.DefaultForbidScheme = "Test";
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { }));
        }).CreateClient();
    }

    private static HttpRequestMessage Orders(string? caller, string? roles)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, "/orders");
        if (caller != null) r.Headers.Add("X-Test-Caller", caller);
        if (roles != null) r.Headers.Add("X-Apim-Caller-Roles", roles);
        return r;
    }

    [Fact]
    public async Task Orders_ok_from_apim_with_scope() =>
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Orders("apim-mi", "orders.read"))).StatusCode);

    [Fact]
    public async Task Orders_forbidden_without_scope() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(Orders("apim-mi", "orders.write"))).StatusCode);

    [Fact]
    public async Task Orders_unauthorized_when_not_from_apim() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Orders(null, "orders.read"))).StatusCode);

    [Fact]
    public async Task Orders_forbidden_for_other_identity() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(Orders("someone-else", "orders.read"))).StatusCode);

    private static HttpRequestMessage Webhook(string body, string? sig, bool verifiedByApim = true)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var r = new HttpRequestMessage(HttpMethod.Post, "/webhooks/orders")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        r.Headers.Add("X-Test-Caller", "apim-mi");
        r.Headers.Add("X-Timestamp", ts);
        r.Headers.Add("X-Signature", sig ?? HmacVerifier.Sign(Secret, ts, body));
        if (verifiedByApim) r.Headers.Add("X-Apim-Webhook-Verified", "true");
        return r;
    }

    [Fact]
    public async Task Webhook_accepted_when_signed() =>
        Assert.Equal(HttpStatusCode.Accepted, (await _client.SendAsync(Webhook("{}", null))).StatusCode);

    [Fact]
    public async Task Webhook_rejected_with_bad_signature() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Webhook("{}", "sha256=00"))).StatusCode);

    [Fact]
    public async Task Webhook_rejected_without_apim_marker() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Webhook("{}", null, verifiedByApim: false))).StatusCode);
}
