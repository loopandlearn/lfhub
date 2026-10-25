using System.Net;
using LFHub.Nightscout;

namespace LFHub.Tests.Nightscout;

/// Which addresses a patient's sources may be fetched from.
public class SourceAddressTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("100.63.255.255", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fec0::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("64:ff9b::808:808", true)]
    [InlineData("2002:c0a8:10a::1", false)]
    public void IsPublic(string address, bool expected) =>
        Assert.Equal(expected, SourceAddress.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task Problem_RefusesAHostThatResolvesToAPrivateAddress()
    {
        Assert.NotNull(await SourceAddress.ProblemAsync("localhost", Ct));
        Assert.NotNull(await SourceAddress.ProblemAsync("10.0.0.5", Ct));
        Assert.NotNull(await SourceAddress.ProblemAsync("[fd00::1]", Ct));
        Assert.Null(await SourceAddress.ProblemAsync("8.8.8.8", Ct));
        Assert.Null(await SourceAddress.ProblemAsync("does-not-resolve.invalid", Ct));
    }

    [Fact]
    public async Task Handler_RefusesToConnectToAPrivateAddressUnlessAllowed()
    {
        using var strict = new HttpClient(SourceAddress.Handler(false));
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => strict.GetAsync("http://127.0.0.1:9/", Ct));
        Assert.Contains("not public", refused.Message, StringComparison.Ordinal);

        using var allowed = new HttpClient(SourceAddress.Handler(true));
        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => allowed.GetAsync("http://127.0.0.1:9/", Ct));
        Assert.DoesNotContain("not public", failed.Message, StringComparison.Ordinal);
    }
}
