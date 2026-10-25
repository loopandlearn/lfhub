using LFHub.Apns;

namespace LFHub.Tests.Apns;

/// When a rejected push makes the client sign a new provider token.
public class ApnsJwtTests
{
    [Theory]
    [InlineData(5, "InvalidProviderToken", false)]
    [InlineData(19, "InvalidProviderToken", false)]
    [InlineData(20, "InvalidProviderToken", true)]
    [InlineData(5, "ExpiredProviderToken", true)]
    [InlineData(5, "TooManyProviderTokenUpdates", false)]
    public void A403_RenewsTheJwtOnlyWhenItExpiredOrIsTwentyMinutesOld(int ageMinutes, string reason, bool renews) =>
        Assert.Equal(renews, ApnsClient.RenewsJwtAfter403(TimeSpan.FromMinutes(ageMinutes), reason));
}
