using LFHub.Db;
using LFHub.Engine.State;

namespace LFHub.Tests.Db;

/// The records that hold secrets print none of them.
public class ModelTests
{
    [Fact]
    public void Patient_PrintsNeitherItsTokenNorItsDexcomPassword()
    {
        var patient = new Patient(Guid.NewGuid(), "Test patient", "https://ns.example.org", "ns-token-value",
            new DexcomCredentials("dexcom-user", "dexcom-secret", "US"));
        var text = patient.ToString();
        Assert.Contains(patient.Id.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("ns-token-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dexcom-secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ApnsConfig_PrintsNeitherThePushTokenNorTheKey()
    {
        var text = new ApnsConfig("a1b2c3d4e5", "sandbox", "org.example.app", "TEAM000000", "KEY0000000", "p8-key-body")
            .ToString();
        Assert.Contains("org.example.app", text, StringComparison.Ordinal);
        Assert.DoesNotContain("a1b2c3d4e5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("p8-key-body", text, StringComparison.Ordinal);
    }
}
