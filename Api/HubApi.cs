using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LFHub.Db;
using LFHub.Engine;
using LFHub.Engine.State;
using LFHub.Nightscout;

namespace LFHub.Api;

/// <param name="Replace">A device this phone takes over, as on a phone change.</param>
/// <param name="CopyFrom">A device whose settings and alarms a new device starts from.</param>
public sealed record ClaimRequest(string SetupToken, string? PatientName, string? DeviceName, Guid? Replace = null,
    Guid? CopyFrom = null);
public sealed record ClaimResponse(Guid DeviceId, string DeviceKey, Guid PatientId, string Role);
public sealed record SetupTokenRequest(string SetupToken);
/// <param name="Replace">A member device this phone takes over, as on a phone change.</param>
/// <param name="CopyFrom">A device whose settings and alarms a new device starts from.</param>
public sealed record JoinRequest(string Code, string? DeviceName, Guid? Replace = null, Guid? CopyFrom = null);
public sealed record InviteCodeRequest(string Code);
public sealed record RoleRequest(string Role);
/// <param name="KeyId">Null, with no key, to push with an admin device's key for the same team and app.</param>
public sealed record ApnsRequest(string Token, string Environment, string BundleId, string TeamId, string? KeyId,
    string? Key);
public sealed record DexcomRequest(string Username, string Password, string? Server);
/// <summary>The patient's sources as the app has them; a missing one is removed.</summary>
public sealed record SourceRequest(string? NightscoutUrl, string? Token, DexcomRequest? Dexcom);
public sealed record TestAlarmRequest(string? Title, string? Sound, int? DelaySeconds);
/// <param name="ActivityToken">Null leaves it as stored; empty means the activity is gone.</param>
/// <param name="RenewBy">The activity's renewal deadline, Unix seconds.</param>
public sealed record LiveActivityRequest(string? ActivityToken, string? PushToStartToken, double? RenewBy);

public static partial class HubApi
{
    private static readonly HashSet<string> SettingsKeys =
        ["badge", "unit", "liveActivity", "alarms", "criticalAlerts", "appVersion", "features"];

    /// <param name="statusKey">
    /// Unlocks the details of /status (counts, problems, `fresh`); null keeps them closed.
    /// </param>
    /// <param name="allowPrivateSources">
    /// Accepts a Nightscout URL on a private, loopback or link-local address, and plain http for
    /// a loopback host (<c>LFHUB_ALLOW_PRIVATE_NIGHTSCOUT</c>).
    /// </param>
    public static void Map(WebApplication app, string setupToken, string? statusKey = null,
        bool allowPrivateSources = false)
    {
        var time = app.Services.GetRequiredService<TimeProvider>();
        // At most five claims a minute across the hub, so the setup token cannot be guessed at speed.
        var claims = new SlidingWindowLimiter<string>(5, TimeSpan.FromMinutes(1), time);
        // At most one test alarm a minute per device.
        var testAlarms = new SlidingWindowLimiter<Guid>(1, TimeSpan.FromMinutes(1), time);
        // Delivery receipts, across the hub: a family's phones post a few a minute.
        var receipts = new SlidingWindowLimiter<string>(120, TimeSpan.FromMinutes(1), time);

        // Every request body is limited; an alarm export is a few KB.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                size.MaxRequestBodySize = MaxBodyBytes;
            if (ctx.Request.ContentLength > MaxBodyBytes)
            {
                await Results.Problem($"the request body is larger than {MaxBodyBytes / 1024} KB", statusCode: 413)
                    .ExecuteAsync(ctx);
                return;
            }
            await next(ctx);
        });

        app.MapGet("/healthz", () => Results.Text("ok\n"));

        // Liveness: the database answers and the engine loop is ticking. A Nightscout
        // outage does not fail it, since restarting the hub would not help.
        app.MapGet("/livez", async (HubStore store, HubEngine engine) =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await store.PingAsync(cts.Token);
            }
            catch
            {
                return Results.Text("database\n", statusCode: 503);
            }
            return engine.IsTicking ? Results.Text("ok\n") : Results.Text("engine\n", statusCode: 503);
        });

        // Public health for external monitors: 200 when healthy, 503 otherwise, on
        // GET and HEAD. `?fresh=1` also requires every patient's latest reading to be
        // younger than the stale threshold. Carries counts only, no identifiers or values.
        app.MapMethods("/status", ["GET", "HEAD"], async (HttpContext ctx, HubStore store, HubEngine engine) =>
        {
            var now = DateTime.UtcNow;
            var problems = new List<string>();

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                // A secret that does not open leaves a device without alarms or the patient unfollowed.
                if (await store.UnopenedSecretsAsync(cts.Token) > 0) problems.Add("secrets");
            }
            catch
            {
                problems.Add("database");
            }

            if (engine.LastTickAt is not { } tick || now - tick > HubEngine.TickInterval * 4)
                problems.Add("engine");

            var streams = engine.AllStatuses();
            var healthy = streams.Count(s => s.Healthy);
            var fresh = streams.Count(s => s.LastReadingAt is { } at && now - at < HubEngine.StaleAfter);
            if (healthy < streams.Count) problems.Add("nightscout");
            // Whether the patient's readings are fresh says something about the patient,
            // so `fresh` and the details are for the holder of the status key only.
            var detailed = statusKey is not null && FixedEquals(ctx.Request.Query["key"], statusKey);
            if (detailed && ctx.Request.Query["fresh"] == "1" && fresh < streams.Count) problems.Add("stale");

            ctx.Response.Headers.CacheControl = "no-store";
            var code = problems.Count == 0 ? 200 : 503;
            var status = problems.Count == 0 ? "ok" : "fail";
            return detailed
                ? Results.Json(new { status, problems, standby = engine.StandingBy, streams = streams.Count, connected = healthy, fresh },
                    statusCode: code)
                : Results.Json(new { status }, statusCode: code);
        });

        var v1 = app.MapGroup("/v1");

        // Claims an empty hub, or joins a claimed one as an admin device: new, a copy of a device or
        // in a device's place. The setup token is the hub's root credential and lives only in its environment.
        v1.MapPost("/claim", async (ClaimRequest req, HttpContext ctx, HubStore store, HubEngine engine,
            ILogger<HubEngine> log) =>
        {
            if (!claims.TryTake("claim")) return TooManyRequests(ctx, "too many claim attempts");
            if (!FixedEquals(req.SetupToken, setupToken))
                return Results.Problem("Invalid setup token", statusCode: 403);
            if (req.Replace is not null && req.CopyFrom is not null)
                return Results.Problem("replace and copyFrom exclude each other", statusCode: 400);

            var patient = await store.PatientAsync();
            if (patient is null && (req.Replace ?? req.CopyFrom) is not null)
                return Results.Problem("unknown device", statusCode: 404);
            var patientId = patient?.Id ?? await store.CreatePatientAsync(Clean(req.PatientName, "Patient"));
            return await JoinAsync(store, engine, log, patientId, req.DeviceName, req.Replace, req.CopyFrom,
                Roles.Admin);
        });

        // The devices a phone joining with the setup token can take over or copy; empty on an
        // unclaimed hub. Counts against the same limit as a claim.
        v1.MapPost("/claim/devices", async (SetupTokenRequest req, HttpContext ctx, HubStore store) =>
        {
            if (!claims.TryTake("claim")) return TooManyRequests(ctx, "too many claim attempts");
            if (!FixedEquals(req.SetupToken, setupToken))
                return Results.Problem("Invalid setup token", statusCode: 403);
            return Results.Ok(JoinableDevices(await store.PatientAsync() is { } patient
                ? await store.DevicesForPatientAsync(patient.Id)
                : []));
        });

        // An admin's invite: a code that lets one phone join as a member, once, within a day.
        v1.MapPost("/invites", async (HttpContext ctx, HubStore store, ILogger<HubEngine> log) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            if (!device.IsAdmin) return Results.Problem("only an admin device can invite", statusCode: 403);
            var code = InviteCode();
            var expiresAt = time.GetUtcNow().UtcDateTime + InviteLifetime;
            await store.CreateInviteAsync(device.PatientId, device.Id, code, expiresAt);
            log.LogInformation("Invite created by {Device}, valid until {Expires:u}", device.Id, expiresAt);
            return Results.Ok(new { code, expiresAt });
        });

        // The devices a phone joining with an invite can take over (members only) or copy.
        v1.MapPost("/join/devices", async (InviteCodeRequest req, HttpContext ctx, HubStore store) =>
        {
            if (!claims.TryTake("claim")) return TooManyRequests(ctx, "too many claim attempts");
            if (await store.InvitePatientAsync(req.Code ?? "", time.GetUtcNow().UtcDateTime) is not { } patientId)
                return Results.Problem("unknown, used or expired invite", statusCode: 403);
            return Results.Ok(JoinableDevices(await store.DevicesForPatientAsync(patientId)));
        });

        // Joins with an invite as a member device, which uses the invite up.
        v1.MapPost("/join", async (JoinRequest req, HttpContext ctx, HubStore store, HubEngine engine,
            ILogger<HubEngine> log) =>
        {
            if (!claims.TryTake("claim")) return TooManyRequests(ctx, "too many claim attempts");
            var now = time.GetUtcNow().UtcDateTime;
            if (await store.InvitePatientAsync(req.Code ?? "", now) is not { } patientId)
                return Results.Problem("unknown, used or expired invite", statusCode: 403);
            if (req.Replace is not null && req.CopyFrom is not null)
                return Results.Problem("replace and copyFrom exclude each other", statusCode: 400);
            if ((req.Replace ?? req.CopyFrom) is { } targetId)
            {
                if (await store.DeviceAsync(targetId) is not { } target || target.PatientId != patientId)
                    return Results.Problem("unknown device", statusCode: 404);
                if (req.Replace is not null && target.IsAdmin)
                    return Results.Problem("an invite cannot take over an admin device", statusCode: 403);
            }
            if (!await store.UseInviteAsync(req.Code!, now))
                return Results.Problem("unknown, used or expired invite", statusCode: 403);
            return await JoinAsync(store, engine, log, patientId, req.DeviceName, req.Replace, req.CopyFrom,
                Roles.Member);
        });

        // A camera that scans an invite's QR code opens this page.
        app.MapGet("/join", () => Results.Text(
            "This is an invite to follow someone in LoopFollow.\n\n" +
            "Open LoopFollow, go to Settings, LoopFollow Hub, Join with Invite, and scan the code again there.\n",
            "text/plain; charset=utf-8"));

        // A delivery receipt: the phone posts back the token an alarm push carried when the push
        // arrived. The token is the credential, so the call takes no device key; it is never logged.
        v1.MapPost("/receipt/{token}", async (string token, HttpContext ctx, HubStore store, ILogger<HubEngine> log) =>
        {
            if (!AlarmReceipts.IsWellFormed(token))
                return Results.Problem("unknown, used or expired receipt", statusCode: 404);
            if (!receipts.TryTake("receipt")) return TooManyRequests(ctx, "too many receipts");
            if (await store.RedeemReceiptAsync(AlarmReceipts.Hash(token), time.GetUtcNow().UtcDateTime) is not { } taken)
                return Results.Problem("unknown, used or expired receipt", statusCode: 404);
            log.LogInformation("Alarm {Alarm} received on {Device}{Recorded}", taken.AlarmId, taken.DeviceId,
                taken.Recorded ? "" : " (not the sounding alarm, or already received)");
            return Results.NoContent();
        });

        v1.MapGet("/me", async (HttpContext ctx, HubStore store, HubEngine engine) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            var patient = await store.GetPatientAsync(device.PatientId);
            var live = engine.Status(device.PatientId);
            var alarmCount = engine.AlarmExport(device)?.Alarms.Count;
            return Results.Ok(new
            {
                hub = new { version = HubInfo.Version, api = HubInfo.Api, features = HubInfo.Features },
                device = new
                {
                    id = device.Id,
                    name = device.Name,
                    role = device.Role,
                    settings = device.Settings,
                    apnsConfigured = device.Apns is not null,
                    apnsKeyShared = device.ApnsKeyShared,
                    apnsEnvironment = device.Apns?.Environment,
                    lastPushAt = device.LastPushAt,
                    lastPushStatus = device.LastPushStatus,
                    lastPushOkAt = device.LastPushOkAt,
                    pushFailingSince = device.PushFailingSince,
                    alarmsReceived = alarmCount is not null,
                    alarmCount = alarmCount ?? 0,
                    lastAlarmPushOk = device.LastAlarmPushOk,
                    disabledAlarmIds = device.HubDisabledAlarms,
                    activeAlarm = device.ActiveAlarm is { } act
                        ? new { alarmId = act.AlarmId, title = act.Title, firedAt = act.FiredAt, lastPushAt = act.LastPushAt, lastPushStatus = act.LastPushStatus, deliveredAt = act.DeliveredAt }
                        : null,
                    liveActivity = new
                    {
                        registered = device.LiveActivity.Token is not null,
                        pushToStartRegistered = device.LiveActivity.PushToStartToken is not null,
                        renewBy = device.LiveActivity.RenewBy,
                        renewalFailed = device.LiveActivity.RenewalFailed,
                        lastPushAt = device.LiveActivity.LastPushAt,
                        lastPushStatus = device.LiveActivity.LastPushStatus,
                    },
                },
                patient = new
                {
                    id = patient!.Id,
                    name = patient.Name,
                    nightscoutHost = patient.NightscoutUrl is { } u ? new Uri(u).Host : null,
                    dexcom = patient.Dexcom is not null,
                    followed = live is not null,
                    hasData = live?.LastReadingAt is not null,
                    sourceRevision = patient.SourceRevision,
                },
                live = new
                {
                    connected = live?.Connected ?? false,
                    lastReadingAt = live?.LastReadingAt,
                    staleAfterSeconds = (int)HubEngine.StaleAfter.TotalSeconds,
                },
                serverTime = DateTime.UtcNow,
            });
        });

        v1.MapPut("/device/apns", async (ApnsRequest req, HttpContext ctx, HubStore store, HubEngine engine) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            if (req.Environment is not ("production" or "sandbox"))
                return Results.Problem("environment must be production or sandbox", statusCode: 400);
            if (string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrWhiteSpace(req.BundleId) ||
                string.IsNullOrWhiteSpace(req.TeamId))
                return Results.Problem("token, bundleId and teamId are required", statusCode: 400);
            if (!PushTokenPattern().IsMatch(req.Token.Trim()))
                return Results.Problem("token must be the device's push token in hex", statusCode: 400);
            if (!BundleIdPattern().IsMatch(req.BundleId.Trim()))
                return Results.Problem("bundleId may hold only letters, digits, '.' and '-'", statusCode: 400);
            if (!AppleIdPattern().IsMatch(req.TeamId.Trim()) ||
                (req.KeyId is not null && !AppleIdPattern().IsMatch(req.KeyId.Trim())))
                return Results.Problem("teamId and keyId must each be 10 upper-case letters or digits", statusCode: 400);
            if (req.Key is not null && Apns.ApnsClient.KeyProblem(req.Key) is { } keyProblem)
                return Results.Problem(keyProblem, statusCode: 400);

            // Without a key id the device asks for an admin device's key; without a key it keeps
            // the one it has, or takes an admin device's.
            if (string.IsNullOrWhiteSpace(req.KeyId) || (req.Key is null && device.Apns is null))
            {
                if (req.Key is not null)
                    return Results.Problem("keyId is required with key", statusCode: 400);
                if (!await store.SetSharedApnsAsync(device.Id, req.Token.Trim(), req.Environment,
                        req.BundleId.Trim(), req.TeamId.Trim()))
                    return Results.Problem("this device needs its own APN key: no admin device has one for this team and app",
                        statusCode: 400);
                engine.Kick(device.PatientId);
                return Results.NoContent();
            }

            await store.SetApnsAsync(device.Id, req.Token.Trim(), req.Environment, req.BundleId.Trim(),
                req.TeamId.Trim(), req.KeyId.Trim(), req.Key);
            engine.Kick(device.PatientId);
            return Results.NoContent();
        });

        v1.MapPut("/device/settings", async (JsonObject patch, HttpContext ctx, HubStore store, HubEngine engine,
            ILogger<HubEngine> log) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            foreach (var (k, v) in patch)
            {
                if (!SettingsKeys.Contains(k))
                    return Results.Problem($"unknown setting '{k}'", statusCode: 400);
                var (valid, rule) = k switch
                {
                    "unit" => (v is JsonValue unit && unit.TryGetValue<string>(out var u) && u is "mgdl" or "mmol",
                        "unit must be mgdl or mmol"),
                    "appVersion" => (IsShortText(v), "appVersion must be a string of at most 32 characters"),
                    "features" => (v is JsonArray list && list.Count <= 32 && list.All(IsShortText),
                        "features must be a list of at most 32 strings of at most 32 characters"),
                    _ => (v is JsonValue flag &&
                          flag.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
                        $"'{k}' must be true or false"),
                };
                if (!valid)
                    return Results.Problem(rule, statusCode: 400);
            }

            if (patch["alarms"] is JsonValue alarmsOn && alarmsOn.GetValue<bool>())
            {
                var features = patch["features"] is JsonArray reported ? reported : device.Settings["features"] as JsonArray;
                var missing = HubInfo.RequiredAppFeatures
                    .Where(f => features?.Any(x => x is JsonValue v && v.TryGetValue<string>(out var s) && s == f) != true)
                    .ToList();
                if (missing.Count > 0)
                    return Results.Problem(
                        $"alarms need a LoopFollow version that supports {string.Join(", ", missing)}: update LoopFollow",
                        statusCode: 409);
                if (engine.AlarmExport(device) is null)
                    return Results.Problem("alarms need the app's alarm list: upload it with PUT /v1/device/alarms first",
                        statusCode: 409);
                // Day and night windows are the phone's; evaluated in another zone they would be wrong.
                if (ClientSettings.ResolveTimeZone(device.ClientSettings) is null)
                    return Results.Problem(
                        "alarms need the phone's time zone: upload client settings with a timeZone this hub knows with PUT /v1/device/clientsettings first",
                        statusCode: 409);
            }

            var settings = await store.MergeSettingsAsync(device.Id, patch);
            log.LogInformation("Hub settings from {Device}: {Settings}", device.Id, settings.ToJsonString());
            engine.Kick(device.PatientId);
            return Results.Ok(settings);
        });

        v1.MapPut("/device/liveactivity", async (LiveActivityRequest req, HttpContext ctx, HubStore store,
            HubEngine engine) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            if (req.RenewBy is > MaxUnixSeconds) return Results.Problem("renewBy is out of range", statusCode: 400);
            var renewBy = req.RenewBy is > 0 ? DateTime.UnixEpoch.AddSeconds(req.RenewBy.Value) : (DateTime?)null;
            await store.ReportLiveActivityAsync(device.Id, req.ActivityToken?.Trim(), req.PushToStartToken?.Trim(),
                renewBy);
            engine.Kick(device.PatientId);
            return Results.NoContent();
        });

        // The phone's settings that change what the hub builds (Storage keys as stored, plus timeZone).
        v1.MapPut("/device/clientsettings", async (JsonObject settings, HttpContext ctx, HubStore store,
            HubEngine engine, ILogger<HubEngine> log) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            await store.SetClientSettingsAsync(device.Id, settings);
            log.LogInformation("Client settings uploaded by {Device}: {Keys}", device.Id,
                string.Join(", ", settings.Select(kv => kv.Key).Order()));
            engine.Kick(device.PatientId);
            return Results.NoContent();
        });

        // The app's AlarmSettingsExport JSON as is. Snoozes travel this way too.
        v1.MapPut("/device/alarms", async (HttpContext ctx, HubStore store, HubEngine engine, ILogger<HubEngine> log) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            if (!HubEngine.TryParseAlarms(json, out var parsed, out var skipped, out var error))
                return Results.Problem(error, statusCode: 400);
            if (parsed.Alarms.Count + skipped > MaxAlarms)
                return Results.Problem($"an alarm export holds at most {MaxAlarms} alarms", statusCode: 400);
            await store.SetAlarmsAsync(device.Id, json,
                parsed.Alarms.Where(a => !a.IsEnabled).Select(a => a.Id).ToList());
            var snoozed = parsed.Alarms.Count(a => a.SnoozedUntil > DateTime.UtcNow);
            log.LogInformation(
                "Alarms uploaded by {Device}: {Count} alarms, {Enabled} enabled, {Snoozed} snoozed, snooze all {SnoozeAll}, mute {Mute}",
                device.Id, parsed.Alarms.Count, parsed.Alarms.Count(a => a.IsEnabled), snoozed,
                parsed.Config.SnoozeUntil > DateTime.UtcNow ? $"until {parsed.Config.SnoozeUntil:HH:mm}Z" : "off",
                parsed.Config.MuteUntil > DateTime.UtcNow ? $"until {parsed.Config.MuteUntil:HH:mm}Z" : "off");
            if (skipped > 0)
                log.LogWarning("Alarms from {Device}: {Skipped} of an unknown type or option skipped", device.Id, skipped);
            engine.Kick(device.PatientId, immediate: true);
            return Results.NoContent();
        });

        // The app snoozed or acknowledged the alarm (any of its snooze paths).
        v1.MapPost("/device/alarm/{alarmId:guid}/handled", async (Guid alarmId, HttpContext ctx, HubStore store,
            HubEngine engine, ILogger<HubEngine> log) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            await store.AlarmHandledAsync(device.Id, alarmId);
            log.LogInformation("Alarm {Alarm} handled on {Device}", alarmId, device.Id);
            engine.Kick(device.PatientId, immediate: true);
            return Results.NoContent();
        });

        // Delivers one alarm push to the calling device, outside the alarm engine, so the
        // user can check that it rings through silent mode. With a delay (at most 120 s)
        // it answers at once and sends later, so the phone can be locked with the app
        // closed first. One a minute per device.
        v1.MapPost("/device/testalarm", async (TestAlarmRequest req, HttpContext ctx, HubStore store,
            Apns.IApnsClient apns, HubEngine engine, ILogger<HubEngine> log, CancellationToken ct) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            if (device.Apns is not { } cfg) return Results.Problem("APNs is not registered", statusCode: 409);
            if (!testAlarms.TryTake(device.Id)) return TooManyRequests(ctx, "one test alarm a minute");
            var title = req.Title ?? "LF Hub test alarm";
            if (req.DelaySeconds is > 0)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(req.DelaySeconds.Value, 120));
                // The delayed send outlives the request, so it does not take the request's token.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(delay, CancellationToken.None);
                    var alarmId = Guid.NewGuid();
                    var receipt = await engine.NewReceiptAsync(device.Id, alarmId, time.GetUtcNow().UtcDateTime);
                    var later = await apns.SendAlarmAsync(cfg, alarmId, title, "Sent from LF Hub", null, req.Sound, "bell", "category",
                        device.CriticalAlerts, true, false, receipt, CancellationToken.None);
                    await engine.TrackPushAsync(device.Id, later, HubEngine.PushKind.Alarm);
                    log.LogInformation("Delayed test alarm to {Device}: {Status}", device.Id, later.Status);
                }, CancellationToken.None);
                return Results.Accepted();
            }
            var testId = Guid.NewGuid();
            var testReceipt = await engine.NewReceiptAsync(device.Id, testId, time.GetUtcNow().UtcDateTime);
            var result = await apns.SendAlarmAsync(cfg, testId, title, "Sent from LF Hub", null, req.Sound, "bell", "category",
                device.CriticalAlerts, true, false, testReceipt, ct);
            await engine.TrackPushAsync(device.Id, result, HubEngine.PushKind.Alarm);
            return result.Delivered
                ? Results.NoContent()
                : Results.Problem($"APNs: {result.Status}", statusCode: 502);
        });

        // The hub's recent log, for LoopFollow's Share Logs. A hub serves one family,
        // so every device of it may read the whole log.
        v1.MapGet("/device/log", async (HttpContext ctx, HubStore store, HubLogBuffer buffer, int? hours) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            var from = DateTime.UtcNow.AddHours(-Math.Clamp(hours ?? 48, 1, 72));
            var lines = buffer.Since(from).Select(l => l.ToString());
            return Results.Text(
                $"LF Hub log, fetched by device {device.Id}, since {from:yyyy-MM-ddTHH:mm}Z\n" +
                string.Join('\n', lines) + "\n");
        });

        // The patient's bundle: what LoopFollow would otherwise fetch from Nightscout and
        // Dexcom itself. With `since`, it answers 304 while the version is unchanged; with
        // `wait` as well (at most 30 s) it holds the request until the bundle changes,
        // so an open app learns of new data at once (long polling, which passes the
        // proxies unbuffered).
        v1.MapGet("/patient/bundle", async (HttpContext ctx, HubStore store, HubEngine engine, long? since,
            int? wait, CancellationToken ct) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            if (since is { } known && known > 0 && known == engine.BundleVersion(device.PatientId))
            {
                if (wait is not > 0) return Results.StatusCode(304);
                var (versions, subscription) = engine.ListenForBundles(device.PatientId);
                using (subscription)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(wait.Value, 30)));
                    try
                    {
                        while (engine.BundleVersion(device.PatientId) == known)
                            await versions.ReadAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return Results.StatusCode(304);
                    }
                }
            }
            return engine.Bundle(device.PatientId) is { } bundle
                ? Results.Text(bundle.ToJsonString(), "application/json")
                : Results.Problem("the patient is not followed yet", statusCode: 503);
        });

        v1.MapDelete("/device", async (HttpContext ctx, HubStore store) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            await store.DeleteDeviceAsync(device.Id);
            return Results.NoContent();
        });

        // The patient's devices, for the app's device list.
        v1.MapGet("/devices", async (HttpContext ctx, HubStore store) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            var devices = await store.DevicesForPatientAsync(device.PatientId);
            return Results.Ok(new
            {
                devices = devices.Select(d => new
                {
                    id = d.Id,
                    name = d.Name,
                    createdAt = d.CreatedAt,
                    lastSeenAt = d.LastSeenAt,
                    appVersion = d.AppVersion,
                    lastPushOkAt = d.LastPushOkAt,
                    pushFailingSince = d.PushFailingSince,
                    role = d.Role,
                    current = d.Id == device.Id,
                }),
            });
        });

        v1.MapDelete("/devices/{deviceId:guid}", async (Guid deviceId, HttpContext ctx, HubStore store,
            HubEngine engine, ILogger<HubEngine> log) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            if (!device.IsAdmin) return Results.Problem("only an admin device can remove another device", statusCode: 403);
            if (!await store.DeletePatientDeviceAsync(device.PatientId, deviceId))
                return Results.Problem("unknown device", statusCode: 404);
            log.LogInformation("Device {Device} removed by {By}", deviceId, device.Id);
            engine.Reload();
            return Results.NoContent();
        });

        v1.MapPut("/devices/{deviceId:guid}/role", async (Guid deviceId, RoleRequest req, HttpContext ctx,
            HubStore store, ILogger<HubEngine> log) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            if (!device.IsAdmin) return Results.Problem("only an admin device can change roles", statusCode: 403);
            if (!Roles.IsValid(req.Role)) return Results.Problem("role must be admin or member", statusCode: 400);
            switch (await store.SetRoleAsync(device.PatientId, deviceId, req.Role))
            {
                case HubStore.RoleChange.UnknownDevice:
                    return Results.Problem("unknown device", statusCode: 404);
                case HubStore.RoleChange.LastAdmin:
                    return Results.Problem("the patient must keep an admin device", statusCode: 409);
            }
            log.LogInformation("Device {Device} made {Role} by {By}", deviceId, req.Role, device.Id);
            return Results.NoContent();
        });

        // The alarm export and client settings as the device last uploaded them, for a phone
        // that took the device over or copied it.
        v1.MapGet("/device/alarms", async (HttpContext ctx, HubStore store) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            return device.AlarmsJson is { } json
                ? Results.Text(json, "application/json")
                : Results.Problem("no alarms uploaded", statusCode: 404);
        });

        v1.MapGet("/device/clientsettings", async (HttpContext ctx, HubStore store) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            return device.ClientSettings.Count > 0
                ? Results.Text(device.ClientSettings.ToJsonString(), "application/json")
                : Results.Problem("no client settings uploaded", statusCode: 404);
        });

        // The patient's sources in full, so every phone can follow the patient on its own when
        // the hub does not answer.
        v1.MapGet("/patient/source", async (HttpContext ctx, HubStore store, ILogger<HubEngine> log) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            var patient = await store.GetPatientAsync(device.PatientId);
            log.LogInformation("Sources for patient {Patient} read by {Device}", device.PatientId, device.Id);
            return Results.Ok(new
            {
                nightscoutUrl = patient?.NightscoutUrl,
                token = patient?.NightscoutToken,
                dexcom = patient?.Dexcom is { } d
                    ? new { username = d.Username, password = d.Password, server = d.Server }
                    : null,
            });
        });

        v1.MapPut("/patient/source", async (SourceRequest req, HttpContext ctx, HubStore store, HubEngine engine,
            ILogger<HubEngine> log, CancellationToken ct) =>
        {
            if (await Authenticate(ctx, store) is not { } device) return Results.Unauthorized();
            if (!device.IsAdmin) return Results.Problem("only an admin device can change the sources", statusCode: 403);

            string? baseUrl = null;
            if (!string.IsNullOrWhiteSpace(req.NightscoutUrl))
            {
                if (!Uri.TryCreate(req.NightscoutUrl.Trim(), UriKind.Absolute, out var url) ||
                    url.Scheme is not ("https" or "http"))
                    return Results.Problem("nightscoutUrl must be an http(s) URL", statusCode: 400);
                // The Nightscout token travels with every request, so plain http is for a local test
                // setup only, and the hub's own network is out of reach unless allowed.
                if (url.Scheme == "http" && !(allowPrivateSources && url.IsLoopback))
                    return Results.Problem("nightscoutUrl must use https", statusCode: 400);
                if (!allowPrivateSources && await SourceAddress.ProblemAsync(url.DnsSafeHost, ct) is { } problem)
                    return Results.Problem($"nightscoutUrl: {problem}", statusCode: 400);
                baseUrl = url.GetLeftPart(UriPartial.Authority) + url.AbsolutePath.TrimEnd('/');
            }

            var dexcom = req.Dexcom is { } d && !string.IsNullOrWhiteSpace(d.Username) && !string.IsNullOrEmpty(d.Password)
                ? new DexcomCredentials(d.Username.Trim(), d.Password, string.IsNullOrWhiteSpace(d.Server) ? "US" : d.Server.Trim())
                : null;
            if (baseUrl is null && dexcom is null)
                return Results.Problem("a Nightscout URL or a Dexcom account is required", statusCode: 400);

            await store.SetSourceAsync(device.PatientId, baseUrl, req.Token?.Trim(), dexcom);
            log.LogInformation("Sources for patient {Patient} set by {Device}: Nightscout {Nightscout}, Dexcom {Dexcom}",
                device.PatientId, device.Id, baseUrl is null ? "none" : new Uri(baseUrl).Host,
                dexcom is null ? "none" : dexcom.Server);
            engine.Reload();
            return Results.NoContent();
        });
    }

    /// How long an invite code works.
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(24);

    /// An invite code: 20 random bytes, base64url.
    private static string InviteCode() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static object JoinableDevices(IEnumerable<Device> devices) => new
    {
        devices = devices.Select(d => new
        {
            id = d.Id,
            name = d.Name,
            role = d.Role,
            createdAt = d.CreatedAt,
            lastSeenAt = d.LastSeenAt,
            appVersion = d.AppVersion,
        }),
    };

    /// Adds a device to the patient with <paramref name="role"/>: new, a copy of
    /// <paramref name="copyFrom"/>, or in <paramref name="replace"/>'s place, which a member
    /// may take only of a member device.
    private static async Task<IResult> JoinAsync(HubStore store, HubEngine engine, ILogger log, Guid patientId,
        string? deviceName, Guid? replace, Guid? copyFrom, string role)
    {
        if (replace is { } replaced)
        {
            var name = string.IsNullOrWhiteSpace(deviceName) ? null : Clean(deviceName, "Device");
            if (await store.ReplaceDeviceAsync(patientId, replaced, name, membersOnly: role != Roles.Admin) is not { } newKey)
                return Results.Problem("unknown device", statusCode: 404);
            if (role == Roles.Admin) await store.SetRoleAsync(patientId, replaced, Roles.Admin);
            log.LogInformation("Device {Device} taken over by a new phone as {Role}", replaced, role);
            engine.Reload();
            return Results.Ok(new ClaimResponse(replaced, newKey, patientId, role));
        }

        var cleanName = Clean(deviceName, "Device");
        if (copyFrom is { } source)
        {
            if (await store.CopyDeviceAsync(patientId, source, cleanName, role) is not var (copyId, copyKey))
                return Results.Problem("unknown device", statusCode: 404);
            log.LogInformation("Device {Device} joined as {Role}, a copy of {Source}", copyId, role, source);
            engine.Reload();
            return Results.Ok(new ClaimResponse(copyId, copyKey, patientId, role));
        }

        var (deviceId, key) = await store.CreateDeviceAsync(patientId, cleanName, role);
        log.LogInformation("Device {Device} joined as {Role}", deviceId, role);
        return Results.Ok(new ClaimResponse(deviceId, key, patientId, role));
    }

    private static bool IsShortText(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length is > 0 and <= 32;

    private static async Task<Device?> Authenticate(HttpContext ctx, HubStore store)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        return await store.AuthenticateAsync(header["Bearer ".Length..].Trim());
    }

    /// The largest request body the hub takes.
    private const int MaxBodyBytes = 256 * 1024;

    /// The most alarms an export may hold; LoopFollow has some 25 alarm types.
    private const int MaxAlarms = 200;

    /// An APNs device token: hex, 32 bytes today, and Apple allows it to grow.
    [GeneratedRegex("^[0-9A-Fa-f]{1,200}\\z")]
    private static partial Regex PushTokenPattern();

    /// The characters Apple allows in a bundle id.
    [GeneratedRegex("^[A-Za-z0-9.-]{1,155}\\z")]
    private static partial Regex BundleIdPattern();

    /// An Apple team id or key id.
    [GeneratedRegex("^[A-Z0-9]{10}\\z")]
    private static partial Regex AppleIdPattern();

    /// 9999-12-31T23:59:59Z, the last second a DateTime holds.
    private const double MaxUnixSeconds = 253_402_300_799;

    private static IResult TooManyRequests(HttpContext ctx, string detail)
    {
        ctx.Response.Headers.RetryAfter = "60";
        return Results.Problem(detail, statusCode: 429);
    }

    private static bool FixedEquals(string? a, string b) =>
        a is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Clean(string? s, string fallback) =>
        string.IsNullOrWhiteSpace(s) ? fallback : s.Trim()[..Math.Min(s.Trim().Length, 64)];
}
