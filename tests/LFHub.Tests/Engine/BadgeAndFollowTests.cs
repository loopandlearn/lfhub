using System.Text.Json.Nodes;
using LFHub.Apns;
using LFHub.Db;
using LFHub.Engine;

namespace LFHub.Tests.Engine;

/// The badge, and how the engine starts, keeps and replaces the patients it follows.
public class BadgeAndFollowTests
{
    [Theory]
    [InlineData(125, false, 125)]
    [InlineData(125, true, 69)]
    [InlineData(97, true, 54)]
    [InlineData(40, true, 22)]
    public void BadgeValue_IsWhatLoopFollowShows(int mgdl, bool mmol, int expected) =>
        Assert.Equal(expected, HubEngine.BadgeValue(mgdl, mmol));

    [Fact]
    public async Task Badge_PushedOnChangeAndZeroWhenStale()
    {
        var h = new EngineHarness();
        var device = h.AddDevice(r => r.Settings = new JsonObject { ["badge"] = true, ["unit"] = "mmol" });
        h.Readings(120, 125);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Equal([69], h.Apns.Badges);
        Assert.Equal(69, device.LastBadge);

        await h.TicksAsync(TimeSpan.FromMinutes(4));
        Assert.Single(h.Apns.Badges);
        await h.TicksAsync(TimeSpan.FromMinutes(8));
        Assert.Equal([69, 0], h.Apns.Badges);
    }

    [Fact]
    public async Task Badge_FailedPushIsRetriedAfterFiveMinutes()
    {
        var h = new EngineHarness();
        var device = h.AddDevice(r => r.Settings = new JsonObject { ["badge"] = true });
        h.Readings(120, 125);
        h.Apns.Result = new PushResult(false, "500 InternalServerError", false, 500);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Null(device.LastBadge);
        h.Apns.Result = new PushResult(true, "200", false, 200);
        await h.TicksAsync(TimeSpan.FromMinutes(4.5));
        Assert.Single(h.Apns.Badges);
        await h.TicksAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, h.Apns.Badges.Count);
        Assert.Equal(125, device.LastBadge);
    }

    [Fact]
    public async Task Badge_TurnedOffClearsTheLastValue()
    {
        var h = new EngineHarness();
        var device = h.AddDevice(r =>
        {
            r.Settings = new JsonObject { ["badge"] = false };
            r.LastBadge = 120;
        });
        await h.ReconcileAsync();
        await h.TickAsync();
        Assert.Null(device.LastBadge);
        Assert.Empty(h.Apns.Badges);
    }

    [Fact]
    public async Task Reconcile_KeepsAPatientWhoseSourceIsUnchanged()
    {
        var h = new EngineHarness();
        h.Readings(120, 125);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        var version = h.Engine.BundleVersion(h.Patient.Id);
        Assert.True(version > 0);

        await h.ReconcileAsync();
        Assert.Equal(version, h.Engine.BundleVersion(h.Patient.Id));
        Assert.NotNull(h.Engine.Status(h.Patient.Id)?.LastReadingAt);
    }

    [Fact]
    public async Task Reconcile_ReplacesAPatientWhoseSourceChangedAndStopsARemovedOne()
    {
        var h = new EngineHarness();
        h.Readings(120, 125);
        await h.ReconcileAsync();
        await h.NewDataAsync();

        // The new runtime starts from the reading the store kept, as Postgres returns it.
        var latest = h.Engine.Status(h.Patient.Id)?.LastReadingAt;
        Assert.NotNull(latest);
        Assert.Equal(latest, h.Store.Patients[0].LastReading?.At);
        h.Store.Patients[0] = h.Store.Patients[0] with { NightscoutToken = "new-token" };
        await h.ReconcileAsync();
        Assert.Equal(0, h.Engine.BundleVersion(h.Patient.Id));
        Assert.Equal(latest, h.Engine.Status(h.Patient.Id)?.LastReadingAt);

        h.Store.Patients.Clear();
        await h.ReconcileAsync();
        Assert.Null(h.Engine.Status(h.Patient.Id));
        Assert.Null(h.Engine.Bundle(h.Patient.Id));
        Assert.Empty(h.Engine.AllStatuses());
    }

    [Fact]
    public async Task Bundle_ListenersHearNewVersions()
    {
        var h = new EngineHarness();
        h.Readings(120, 125);
        await h.ReconcileAsync();
        var (versions, subscription) = h.Engine.ListenForBundles(h.Patient.Id);
        using (subscription)
        {
            await h.NewDataAsync();
            Assert.True(versions.TryRead(out var version));
            Assert.True(version > 0);
        }
        Assert.True(versions.Completion.IsCompleted);
    }

    [Fact]
    public async Task Status_CountsAFollowedPatient()
    {
        var h = new EngineHarness();
        h.Readings(120, 125);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        var status = Assert.Single(h.Engine.AllStatuses());
        Assert.True(status.Connected);
        Assert.True(status.Healthy);
        Assert.Equal(h.Now, status.LastReadingAt);
    }

    [Fact]
    public async Task FailingNightscout_IsLoggedWithoutStoppingTheDevices()
    {
        var h = new EngineHarness();
        var device = h.AddDevice(r => r.Settings = new JsonObject { ["badge"] = true });
        h.Nightscout.Route("/api/v1/entries.json", "not json");
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Equal([0], h.Apns.Badges);
        Assert.Equal(0, device.LastBadge);
    }

    [Fact]
    public async Task UnknownTimeZone_IsLoggedOnce()
    {
        var h = new EngineHarness();
        h.AddDevice(r => r.ClientSettings = new JsonObject { ["timeZone"] = "Nowhere/Unknown" });
        await h.ReconcileAsync();
        await h.TickAsync();
        await h.TickAsync();
        Assert.Single(h.Logs.Logger.Lines, l => l.Contains("Nowhere/Unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingTimeZoneWithAlarmsOn_IsLoggedOncePerDeviceUntilItArrives()
    {
        var h = new EngineHarness();
        var device = h.AddDevice(r => r.Settings = new JsonObject { ["alarms"] = true });
        h.AddDevice();
        await h.ReconcileAsync();
        await h.TickAsync();
        await h.TickAsync();
        bool Warning(string l) => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("(none)", StringComparison.Ordinal);
        Assert.Single(h.Logs.Logger.Lines, l => Warning(l) && l.Contains(device.Id.ToString(), StringComparison.Ordinal));
        Assert.Single(h.Logs.Logger.Lines, Warning);

        device.ClientSettings = new JsonObject { ["timeZone"] = "Europe/Stockholm" };
        await h.TickAsync();
        device.ClientSettings = new JsonObject();
        await h.TickAsync();
        Assert.Equal(2, h.Logs.Logger.Lines.Count(Warning));
    }

    [Fact]
    public async Task Reconcile_SkipsAPatientThatCannotStartAndFollowsTheOthers()
    {
        var h = new EngineHarness();
        // Dexcom credentials without a username or password are no source.
        var broken = new Patient(Guid.NewGuid(), "Broken patient", null, null, new LFHub.Engine.State.DexcomCredentials("", "", "US"));
        h.Store.Patients.Insert(0, broken);
        await h.ReconcileAsync();
        Assert.Null(h.Engine.Status(broken.Id));
        Assert.NotNull(h.Engine.Status(h.Patient.Id));
        Assert.Contains(h.Logs.Logger.Lines, l => l.StartsWith("Error", StringComparison.Ordinal) &&
                                                  l.Contains(broken.Id.ToString(), StringComparison.Ordinal));

        // The next reconcile tries it again, and keeps the other.
        await h.ReconcileAsync();
        Assert.Null(h.Engine.Status(broken.Id));
        Assert.NotNull(h.Engine.Status(h.Patient.Id));
    }

    [Fact]
    public void Patient_WithoutSourceIsNotFollowed()
    {
        var p = new Patient(Guid.NewGuid(), "Test patient", null, null);
        Assert.False(p.HasSource);
    }
}
