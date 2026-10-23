using AuthPatterns.Api;

namespace AuthPatterns.Api.Tests;

public class HmacVerifierTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly string Ts = Now.ToUnixTimeSeconds().ToString();
    private const string Body = "{\"event\":\"order.created\"}";

    private static HmacVerifier Verifier(params string[] secrets) => new(secrets, new FixedClock(Now));

    [Fact]
    public void Accepts_valid_signature() =>
        Assert.True(Verifier("primary").Verify(Ts, HmacVerifier.Sign("primary", Ts, Body), Body));

    [Fact]
    public void Accepts_secondary_secret_during_rotation() =>
        Assert.True(Verifier("new", "old").Verify(Ts, HmacVerifier.Sign("old", Ts, Body), Body));

    [Fact]
    public void Rejects_unknown_secret() =>
        Assert.False(Verifier("new", "old").Verify(Ts, HmacVerifier.Sign("other", Ts, Body), Body));

    [Fact]
    public void Rejects_tampered_body() =>
        Assert.False(Verifier("primary").Verify(Ts, HmacVerifier.Sign("primary", Ts, Body), Body + " "));

    [Fact]
    public void Rejects_stale_timestamp()
    {
        var old = (Now.ToUnixTimeSeconds() - 301).ToString();
        Assert.False(Verifier("primary").Verify(old, HmacVerifier.Sign("primary", old, Body), Body));
    }

    [Fact]
    public void Rejects_timestamp_swapped_after_signing()
    {
        var sig = HmacVerifier.Sign("primary", Ts, Body);
        Assert.False(Verifier("primary").Verify((Now.ToUnixTimeSeconds() + 1).ToString(), sig, Body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("md5=abc")]
    public void Rejects_malformed_signature(string? sig) =>
        Assert.False(Verifier("primary").Verify(Ts, sig, Body));
}
