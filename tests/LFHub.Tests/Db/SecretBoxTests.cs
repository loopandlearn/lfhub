using System.Security.Cryptography;
using LFHub.Db;

namespace LFHub.Tests.Db;

public class SecretBoxTests
{
    private static SecretBox NewBox() => new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public void Seal_RoundTripsWithAFreshNonceEachTime()
    {
        var box = NewBox();
        var a = box.Seal("secret value");
        var b = box.Seal("secret value");
        Assert.NotEqual(a, b);
        Assert.Equal("secret value", box.Open(a));
        Assert.Equal("secret value", box.Open(b));
        Assert.Equal(12 + 16 + "secret value".Length, Convert.FromBase64String(a).Length);
    }

    [Fact]
    public void Open_RejectsTamperedText()
    {
        var box = NewBox();
        var bytes = Convert.FromBase64String(box.Seal("secret value"));
        bytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => box.Open(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Open_RejectsAnotherKey() =>
        Assert.ThrowsAny<CryptographicException>(() => NewBox().Open(NewBox().Seal("secret value")));

    [Fact]
    public void Key_MustBe32Bytes() =>
        Assert.Throws<InvalidOperationException>(() => new SecretBox(Convert.ToBase64String(new byte[16])));

    [Fact]
    public void DeviceKey_IsRandomAndUrlSafe()
    {
        var a = HubStore.NewDeviceKey();
        Assert.NotEqual(a, HubStore.NewDeviceKey());
        Assert.Matches("^lfh_[A-Za-z0-9_-]{43}$", a);
    }
}
