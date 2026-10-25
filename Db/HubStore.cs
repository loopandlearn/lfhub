using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace LFHub.Db;

/// <summary>
/// All persistence for the hub. Secrets are sealed with <see cref="SecretBox"/>
/// before they reach the database; device keys are stored only as SHA-256.
/// </summary>
public sealed class HubStore : IHubStore
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE lfhub.patients (
            id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            name             TEXT NOT NULL,
            ns_url           TEXT,
            ns_token_sealed  TEXT,
            created_at       TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE TABLE lfhub.devices (
            id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            patient_id        UUID NOT NULL REFERENCES lfhub.patients(id) ON DELETE CASCADE,
            name              TEXT NOT NULL,
            role              TEXT NOT NULL,
            key_hash          TEXT NOT NULL UNIQUE,
            settings          JSONB NOT NULL DEFAULT '{}',
            apns_token        TEXT,
            apns_env          TEXT,
            apns_bundle_id    TEXT,
            apns_team_id      TEXT,
            apns_key_id       TEXT,
            apns_key_sealed   TEXT,
            last_badge        INT,
            last_push_at      TIMESTAMPTZ,
            last_push_status  TEXT,
            last_seen_at      TIMESTAMPTZ,
            created_at        TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE INDEX ON lfhub.devices (patient_id);
        """,
        """
        ALTER TABLE lfhub.devices
            ADD COLUMN la_token                TEXT,
            ADD COLUMN la_push_to_start_token  TEXT,
            ADD COLUMN la_renew_by             TIMESTAMPTZ,
            ADD COLUMN la_renewal_failed       BOOLEAN NOT NULL DEFAULT false,
            ADD COLUMN la_last_fingerprint     TEXT,
            ADD COLUMN la_last_push_at         TIMESTAMPTZ,
            ADD COLUMN la_last_push_status     TEXT;
        """,
        """
        ALTER TABLE lfhub.devices
            ADD COLUMN client_settings  JSONB NOT NULL DEFAULT '{}',
            ADD COLUMN alarms_json      TEXT,
            ADD COLUMN alarm_state      JSONB,
            ADD COLUMN active_alarm     JSONB,
            ADD COLUMN hub_disabled_alarms JSONB NOT NULL DEFAULT '[]';
        """,
        """
        ALTER TABLE lfhub.patients
            ADD COLUMN dexcom_sealed TEXT;
        """,
        """
        ALTER TABLE lfhub.devices
            ADD COLUMN alarm_rev INT NOT NULL DEFAULT 0;
        """,
        // A hub holds one patient: the oldest is kept, the others' devices move to it and
        // the others are removed, and the index then holds it to one.
        """
        UPDATE lfhub.devices SET patient_id = (SELECT id FROM lfhub.patients ORDER BY created_at, id LIMIT 1)
            WHERE patient_id <> (SELECT id FROM lfhub.patients ORDER BY created_at, id LIMIT 1);
        DELETE FROM lfhub.patients WHERE id <> (SELECT id FROM lfhub.patients ORDER BY created_at, id LIMIT 1);
        CREATE UNIQUE INDEX patients_one_per_hub ON lfhub.patients ((true));
        """,
        """
        ALTER TABLE lfhub.devices
            ADD COLUMN last_push_ok_at     TIMESTAMPTZ,
            ADD COLUMN push_failing_since  TIMESTAMPTZ,
            ADD COLUMN last_alarm_push_ok  BOOLEAN;
        """,
        """
        ALTER TABLE lfhub.devices
            ADD COLUMN push_token_gone BOOLEAN NOT NULL DEFAULT false;
        """,
        """
        ALTER TABLE lfhub.patients
            ADD COLUMN last_reading_at    TIMESTAMPTZ,
            ADD COLUMN last_reading_mgdl  INT;
        """,
        // A delivery receipt token, by its SHA-256, and the alarm firing it was sent for
        // (fired_at as the active_alarm JSON holds it).
        """
        CREATE TABLE lfhub.alarm_receipts (
            token_hash  TEXT PRIMARY KEY,
            device_id   UUID NOT NULL REFERENCES lfhub.devices(id) ON DELETE CASCADE,
            alarm_id    UUID NOT NULL,
            fired_at    TEXT NOT NULL,
            expires_at  TIMESTAMPTZ NOT NULL
        );
        CREATE INDEX ON lfhub.alarm_receipts (expires_at);
        """,
        """
        ALTER TABLE lfhub.devices ALTER COLUMN role SET DEFAULT 'admin';
        """,
        // An invite code, by its SHA-256; it is removed when used. apns_key_shared marks a
        // device that pushes with an admin device's APNs key.
        """
        ALTER TABLE lfhub.patients ADD COLUMN source_rev INT NOT NULL DEFAULT 0;
        ALTER TABLE lfhub.devices ADD COLUMN apns_key_shared BOOLEAN NOT NULL DEFAULT false;
        CREATE TABLE lfhub.invites (
            code_hash   TEXT PRIMARY KEY,
            patient_id  UUID NOT NULL REFERENCES lfhub.patients(id) ON DELETE CASCADE,
            created_by  UUID,
            created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
            expires_at  TIMESTAMPTZ NOT NULL
        );
        """,
    ];

    /// <summary>The schema version a migrated database is at.</summary>
    internal static int SchemaVersion => Migrations.Length;

    private readonly NpgsqlDataSource _ds;
    private readonly SecretBox _box;
    private readonly ILogger _log;

    public HubStore(NpgsqlDataSource ds, SecretBox box, ILogger<HubStore>? log = null)
    {
        _ds = ds;
        _box = box;
        _log = log ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>
    /// Opens a sealed value, or null when it does not open under the current master
    /// key. The app then sends the secret again, which seals it under the current key.
    /// A value that does not open is logged once per row and secret for the process,
    /// since rows are read several times a minute.
    /// </summary>
    private string? OpenOrNull(string sealedText, string what, Guid rowId)
    {
        if (TryOpen(sealedText) is { } opened) return opened;
        if (_unopenedLogged.TryAdd((what, rowId), true))
            _log.LogError("The stored {What} of {Row} does not open with LFHUB_MASTER_KEY; it is treated as unset",
                what, rowId);
        return null;
    }

    private string? TryOpen(string sealedText)
    {
        try
        {
            return _box.Open(sealedText);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string What, Guid Row), bool> _unopenedLogged = new();

    /// <summary>
    /// How many stored secrets (Nightscout tokens, Dexcom accounts, APNs keys) do not open
    /// under the current master key. A query, so it also fails when the database does.
    /// </summary>
    public async Task<int> UnopenedSecretsAsync(CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT ns_token_sealed FROM lfhub.patients WHERE ns_token_sealed IS NOT NULL " +
            "UNION ALL SELECT dexcom_sealed FROM lfhub.patients WHERE dexcom_sealed IS NOT NULL " +
            "UNION ALL SELECT apns_key_sealed FROM lfhub.devices WHERE apns_key_sealed IS NOT NULL");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var unopened = 0;
        while (await r.ReadAsync(ct))
            if (TryOpen(r.GetString(0)) is null) unopened++;
        return unopened;
    }

    public void Migrate() => Migrate(Migrations.Length);

    /// <summary>
    /// Migrates up to schema version <paramref name="target"/>, each migration in its own
    /// transaction, under a Postgres advisory lock, so hubs starting together against one
    /// database migrate one after the other.
    /// </summary>
    internal void Migrate(int target)
    {
        using var conn = _ds.OpenConnection();
        using (var cmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conn))
        {
            cmd.Parameters.AddWithValue("k", MigrationLock);
            cmd.ExecuteNonQuery();
        }
        try
        {
            using (var cmd = new NpgsqlCommand(
                       "CREATE SCHEMA IF NOT EXISTS lfhub; " +
                       "CREATE TABLE IF NOT EXISTS lfhub.schema_version (version INT NOT NULL);", conn))
                cmd.ExecuteNonQuery();

            int current;
            using (var cmd = new NpgsqlCommand("SELECT coalesce(max(version), 0) FROM lfhub.schema_version", conn))
                current = Convert.ToInt32(cmd.ExecuteScalar());

            for (var v = current; v < Math.Min(target, Migrations.Length); v++)
            {
                using var tx = conn.BeginTransaction();
                using (var cmd = new NpgsqlCommand(Migrations[v], conn, tx))
                    cmd.ExecuteNonQuery();
                using (var cmd = new NpgsqlCommand("INSERT INTO lfhub.schema_version VALUES (@v)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("v", v + 1);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
        finally
        {
            using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
            unlock.Parameters.AddWithValue("k", MigrationLock);
            unlock.ExecuteNonQuery();
        }
    }

    /// The advisory lock key migrations hold ("lfhubmig" in ASCII).
    private const long MigrationLock = 0x6C66_6875_626D_6967;

    /// The advisory lock key the instance running the engine holds ("lfhubeng" in ASCII).
    private const long EngineLock = 0x6C66_6875_6265_6E67;

    /// <summary>
    /// Takes the engine lock as a session-level advisory lock on a connection of its own, which
    /// holds it until the handle is disposed or the connection drops.
    /// </summary>
    public async Task<IEngineLock?> TryLockEngineAsync(CancellationToken ct)
    {
        var conn = await _ds.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn);
            cmd.Parameters.AddWithValue("k", EngineLock);
            if (await cmd.ExecuteScalarAsync(ct) is true) return new PostgresEngineLock(conn);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
        await conn.DisposeAsync();
        return null;
    }

    private sealed class PostgresEngineLock(NpgsqlConnection conn) : IEngineLock
    {
        public async Task<bool> IsHeldAsync(CancellationToken ct)
        {
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync(ct);
                return true;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
                cmd.Parameters.AddWithValue("k", EngineLock);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception)
            {
                // A connection that dropped has released the lock with it.
            }
            await conn.DisposeAsync();
        }
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
    }

    public static string NewDeviceKey() =>
        "lfh_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    // MARK: patients

    private const string PatientColumns =
        "id, name, ns_url, ns_token_sealed, dexcom_sealed, last_reading_at, last_reading_mgdl, source_rev";

    /// <summary>
    /// The hub's patient, if claimed. A hub serves one family and one patient
    /// (enforced by the patients_one_per_hub index); a host runs a hub per family.
    /// </summary>
    public async Task<Patient?> PatientAsync()
    {
        await using var cmd = _ds.CreateCommand(
            $"SELECT {PatientColumns} FROM lfhub.patients ORDER BY created_at LIMIT 1");
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadPatient(r) : null;
    }

    public async Task<Patient?> GetPatientAsync(Guid id)
    {
        await using var cmd = _ds.CreateCommand(
            $"SELECT {PatientColumns} FROM lfhub.patients WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadPatient(r) : null;
    }

    public async Task<List<Patient>> PatientsWithSourceAsync()
    {
        var list = new List<Patient>();
        await using var cmd = _ds.CreateCommand(
            $"SELECT {PatientColumns} FROM lfhub.patients WHERE ns_url IS NOT NULL OR dexcom_sealed IS NOT NULL");
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(ReadPatient(r));
        return list;
    }

    public async Task<Guid> CreatePatientAsync(string name)
    {
        await using var cmd = _ds.CreateCommand("INSERT INTO lfhub.patients (name) VALUES (@n) RETURNING id");
        cmd.Parameters.AddWithValue("n", name);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Sets the patient's sources and moves their revision. A null Nightscout URL or Dexcom
    /// account removes that source. The Dexcom account is sealed as one JSON value.
    /// </summary>
    public async Task SetSourceAsync(Guid patientId, string? url, string? token,
        LFHub.Engine.State.DexcomCredentials? dexcom)
    {
        await using var cmd = _ds.CreateCommand(
            "UPDATE lfhub.patients SET ns_url = @u, ns_token_sealed = @t, dexcom_sealed = @d, " +
            "source_rev = source_rev + 1 WHERE id = @id");
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = (object?)url ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text)
        {
            Value = url is null || string.IsNullOrEmpty(token) ? DBNull.Value : _box.Seal(token),
        });
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text)
        {
            Value = dexcom is null
                ? DBNull.Value
                : _box.Seal(new JsonObject
                {
                    ["username"] = dexcom.Username,
                    ["password"] = dexcom.Password,
                    ["server"] = dexcom.Server,
                }.ToJsonString()),
        });
        cmd.Parameters.AddWithValue("id", patientId);
        await cmd.ExecuteNonQueryAsync();
    }

    private Patient ReadPatient(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.IsDBNull(3) ? null : OpenOrNull(r.GetString(3), "Nightscout token", r.GetGuid(0)),
        r.IsDBNull(4) ? null : OpenOrNull(r.GetString(4), "Dexcom account", r.GetGuid(0)) is { } dexcom ? ReadDexcom(dexcom) : null,
        r.IsDBNull(5) || r.IsDBNull(6) ? null : (r.GetDateTime(5), r.GetInt32(6)),
        r.GetInt32(7));

    /// <summary>
    /// Keeps the patient's latest reading, so a restarted hub starts from it while its
    /// sources are down. Writes only when it differs from the stored one.
    /// </summary>
    public async Task SaveLastReadingAsync(Guid patientId, DateTime at, int mgdl)
    {
        await using var cmd = _ds.CreateCommand(
            "UPDATE lfhub.patients SET last_reading_at = @at, last_reading_mgdl = @v " +
            "WHERE id = @id AND (last_reading_at IS DISTINCT FROM @at OR last_reading_mgdl IS DISTINCT FROM @v)");
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Integer) { Value = mgdl });
        cmd.Parameters.AddWithValue("id", patientId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static LFHub.Engine.State.DexcomCredentials? ReadDexcom(string json)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        return new LFHub.Engine.State.DexcomCredentials(o["username"]!.GetValue<string>(),
            o["password"]!.GetValue<string>(), o["server"]?.GetValue<string>() ?? "US");
    }

    // MARK: devices

    private const string DeviceColumns =
        "id, patient_id, name, settings::text, apns_token, apns_env, apns_bundle_id, apns_team_id, " +
        "apns_key_id, apns_key_sealed, last_badge, last_push_at, last_push_status, last_seen_at, " +
        "la_token, la_push_to_start_token, la_renew_by, la_renewal_failed, la_last_fingerprint, la_last_push_at, " +
        "la_last_push_status, client_settings::text, alarms_json, alarm_state::text, active_alarm::text, " +
        "hub_disabled_alarms::text, alarm_rev, last_push_ok_at, push_failing_since, last_alarm_push_ok, created_at, role, apns_key_shared";

    public async Task<(Guid Id, string Key)> CreateDeviceAsync(Guid patientId, string name, string role)
    {
        var key = NewDeviceKey();
        await using var cmd = _ds.CreateCommand(
            "INSERT INTO lfhub.devices (patient_id, name, role, key_hash) VALUES (@p, @n, @r, @h) RETURNING id");
        cmd.Parameters.AddWithValue("p", patientId);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("r", role);
        cmd.Parameters.AddWithValue("h", Hash(key));
        return ((Guid)(await cmd.ExecuteScalarAsync())!, key);
    }

    /// <summary>
    /// A new device whose hub settings (less the app's version and features), client settings
    /// and alarm export are those of <paramref name="sourceId"/>; null when the patient has no such device.
    /// </summary>
    public async Task<(Guid Id, string Key)?> CopyDeviceAsync(Guid patientId, Guid sourceId, string name, string role)
    {
        var key = NewDeviceKey();
        await using var cmd = _ds.CreateCommand(
            "INSERT INTO lfhub.devices (patient_id, name, role, key_hash, settings, client_settings, alarms_json) " +
            "SELECT patient_id, @n, @r, @h, settings - 'appVersion' - 'features', client_settings, alarms_json " +
            "FROM lfhub.devices WHERE id = @s AND patient_id = @p RETURNING id");
        cmd.Parameters.AddWithValue("p", patientId);
        cmd.Parameters.AddWithValue("r", role);
        cmd.Parameters.AddWithValue("s", sourceId);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("h", Hash(key));
        return await cmd.ExecuteScalarAsync() is Guid id ? (id, key) : null;
    }

    /// <summary>
    /// Hands <paramref name="deviceId"/> to a new phone: a new key, so the old one stops working,
    /// and no push or Live Activity registration until the new phone sends its own. Settings,
    /// alarms and alarm state stay. With <paramref name="membersOnly"/> only a member device is
    /// handed over. Null when the patient has no such device.
    /// </summary>
    public async Task<string?> ReplaceDeviceAsync(Guid patientId, Guid deviceId, string? name, bool membersOnly = false)
    {
        var key = NewDeviceKey();
        await using var cmd = _ds.CreateCommand(
            "UPDATE lfhub.devices SET key_hash = @h, name = coalesce(@n, name), last_seen_at = now(), " +
            "settings = settings - 'appVersion' - 'features', " +
            "apns_token = NULL, apns_env = NULL, apns_bundle_id = NULL, apns_team_id = NULL, apns_key_id = NULL, " +
            "apns_key_sealed = NULL, apns_key_shared = false, last_badge = NULL, push_token_gone = false, " +
            "push_failing_since = NULL, la_token = NULL, la_push_to_start_token = NULL, la_renew_by = NULL, " +
            "la_renewal_failed = false, la_last_fingerprint = NULL " +
            "WHERE id = @id AND patient_id = @p AND (NOT @m OR role <> 'admin') RETURNING id");
        cmd.Parameters.AddWithValue("m", membersOnly);
        cmd.Parameters.AddWithValue("p", patientId);
        cmd.Parameters.AddWithValue("id", deviceId);
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = (object?)name ?? DBNull.Value });
        cmd.Parameters.AddWithValue("h", Hash(key));
        return await cmd.ExecuteScalarAsync() is Guid ? key : null;
    }

    /// <summary>Resolves a bearer key to its device and stamps it as seen.</summary>
    public async Task<Device?> AuthenticateAsync(string key)
    {
        await using var cmd = _ds.CreateCommand(
            $"UPDATE lfhub.devices SET last_seen_at = now() WHERE key_hash = @h RETURNING {DeviceColumns}");
        cmd.Parameters.AddWithValue("h", Hash(key));
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadDevice(r) : null;
    }

    public async Task<List<Device>> DevicesForPatientAsync(Guid patientId)
    {
        var list = new List<Device>();
        await using var cmd = _ds.CreateCommand(
            $"SELECT {DeviceColumns} FROM lfhub.devices WHERE patient_id = @p ORDER BY created_at");
        cmd.Parameters.AddWithValue("p", patientId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(ReadDevice(r));
        return list;
    }

    public async Task<Device?> DeviceAsync(Guid deviceId)
    {
        await using var cmd = _ds.CreateCommand($"SELECT {DeviceColumns} FROM lfhub.devices WHERE id = @id");
        cmd.Parameters.AddWithValue("id", deviceId);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadDevice(r) : null;
    }

    public async Task<JsonObject> MergeSettingsAsync(Guid deviceId, JsonObject patch)
    {
        await using var cmd = _ds.CreateCommand(
            "UPDATE lfhub.devices SET settings = settings || @s WHERE id = @id RETURNING settings::text");
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Jsonb) { Value = patch.ToJsonString() });
        cmd.Parameters.AddWithValue("id", deviceId);
        var json = (string)(await cmd.ExecuteScalarAsync())!;
        return JsonNode.Parse(json)!.AsObject();
    }

    /// <summary>
    /// Stores the device's APNs registration. A null <paramref name="key"/> keeps the stored key,
    /// so a token refresh need not resend the .p8. A device that sends its own key stops sharing
    /// one; devices sharing an admin device's key for the same team and bundle id follow it.
    /// </summary>
    public async Task SetApnsAsync(Guid deviceId, string token, string env, string bundleId, string teamId,
        string keyId, string? key)
    {
        var sealedKey = key is null ? null : _box.Seal(key);
        await using var cmd = _ds.CreateCommand(
            "UPDATE lfhub.devices SET apns_token = @t, apns_env = @e, apns_bundle_id = @b, apns_team_id = @tm, " +
            "apns_key_id = @k, apns_key_sealed = coalesce(@ks, apns_key_sealed), " +
            "apns_key_shared = apns_key_shared AND @ks IS NULL, last_badge = NULL, " +
            "last_push_status = NULL, push_token_gone = false WHERE id = @id");
        cmd.Parameters.AddWithValue("t", token);
        cmd.Parameters.AddWithValue("e", env);
        cmd.Parameters.AddWithValue("b", bundleId);
        cmd.Parameters.AddWithValue("tm", teamId);
        cmd.Parameters.AddWithValue("k", keyId);
        cmd.Parameters.Add(new NpgsqlParameter("ks", NpgsqlDbType.Text) { Value = (object?)sealedKey ?? DBNull.Value });
        cmd.Parameters.AddWithValue("id", deviceId);
        await cmd.ExecuteNonQueryAsync();

        if (sealedKey is not null)
        {
            await using var follow = _ds.CreateCommand(
                "UPDATE lfhub.devices d SET apns_key_id = @k, apns_key_sealed = @ks FROM lfhub.devices a " +
                "WHERE a.id = @id AND a.role = 'admin' AND d.patient_id = a.patient_id AND d.apns_key_shared " +
                "AND d.apns_team_id = @tm AND d.apns_bundle_id = @b");
            follow.Parameters.AddWithValue("k", keyId);
            follow.Parameters.AddWithValue("ks", sealedKey);
            follow.Parameters.AddWithValue("id", deviceId);
            follow.Parameters.AddWithValue("tm", teamId);
            follow.Parameters.AddWithValue("b", bundleId);
            await follow.ExecuteNonQueryAsync();
        }

        // A reinstalled app claims again with the same push token: the push token
        // identifies the phone, so it keeps only the row that registered it last.
        await using var dedupe = _ds.CreateCommand(
            "DELETE FROM lfhub.devices WHERE id <> @id AND apns_token = @t AND apns_bundle_id = @b");
        dedupe.Parameters.AddWithValue("id", deviceId);
        dedupe.Parameters.AddWithValue("t", token);
        dedupe.Parameters.AddWithValue("b", bundleId);
        await dedupe.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Removes devices not seen for <paramref name="age"/> whose last push to their own token
    /// APNs rejected as gone (the app was deleted, or the phone reset or sold). A device
    /// whose pushes are still accepted keeps its alarms however long the app stays closed.
    /// </summary>
    public async Task<int> PruneUnseenDevicesAsync(TimeSpan age)
    {
        await using var cmd = _ds.CreateCommand(
            "DELETE FROM lfhub.devices WHERE coalesce(last_seen_at, created_at) < now() - @age AND push_token_gone");
        cmd.Parameters.AddWithValue("age", age);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The app handled the alarm (snooze or acknowledge): it stops sounding and stops
    /// being current, with its title override, so the hub neither repeats it nor holds
    /// it back from firing again.
    /// Moves the alarm revision, so an evaluation that read the device before this
    /// does not write its older state back.
    /// </summary>
    public async Task AlarmHandledAsync(Guid deviceId, Guid alarmId)
    {
        await using var cmd = _ds.CreateCommand(
            """
            UPDATE lfhub.devices SET
                active_alarm = CASE WHEN active_alarm->>'alarmId' = @a THEN NULL ELSE active_alarm END,
                alarm_state  = CASE WHEN alarm_state->>'CurrentAlarmId' = @a
                                    THEN alarm_state || '{"CurrentAlarmId": null, "CurrentAlarmTitleOverride": null}'
                                    ELSE alarm_state END,
                alarm_rev    = alarm_rev + 1
            WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("a", alarmId.ToString());
        cmd.Parameters.AddWithValue("id", deviceId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RecordPushAsync(Guid deviceId, int? badge, string status, bool delivered)
    {
        await using var cmd = _ds.CreateCommand(delivered
            ? "UPDATE lfhub.devices SET last_badge = @b, last_push_at = now(), last_push_status = @s WHERE id = @id"
            : "UPDATE lfhub.devices SET last_push_at = now(), last_push_status = @s WHERE id = @id");
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Integer) { Value = (object?)badge ?? DBNull.Value });
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("id", deviceId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Records whether a push of any kind to the device was accepted: an accepted one
    /// stamps <c>last_push_ok_at</c> and clears <c>push_failing_since</c>; a rejected one
    /// sets <c>push_failing_since</c> unless it is set. An alarm push also sets
    /// <c>last_alarm_push_ok</c>. <paramref name="tokenGone"/> sets <c>push_token_gone</c>:
    /// whether the last push to the device's own token was rejected as gone. Returns <c>push_failing_since</c> as it now stands.
    /// </summary>
    public async Task<DateTime?> RecordPushOutcomeAsync(Guid deviceId, DateTime at, bool accepted, bool alarm,
        bool tokenGone)
    {
        await using var cmd = _ds.CreateCommand(
            """
            UPDATE lfhub.devices SET
                last_push_ok_at    = CASE WHEN @ok THEN @at ELSE last_push_ok_at END,
                push_failing_since = CASE WHEN @ok THEN NULL ELSE coalesce(push_failing_since, @at) END,
                last_alarm_push_ok = CASE WHEN @alarm THEN @ok ELSE last_alarm_push_ok END,
                push_token_gone    = @gone
            WHERE id = @id
            RETURNING push_failing_since
            """);
        cmd.Parameters.AddWithValue("ok", accepted);
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
        cmd.Parameters.AddWithValue("alarm", alarm);
        cmd.Parameters.AddWithValue("gone", tokenGone);
        cmd.Parameters.AddWithValue("id", deviceId);
        return await cmd.ExecuteScalarAsync() is DateTime since ? since : null;
    }

    public async Task ClearLastBadgeAsync(Guid deviceId)
    {
        await using var cmd = _ds.CreateCommand("UPDATE lfhub.devices SET last_badge = NULL WHERE id = @id");
        cmd.Parameters.AddWithValue("id", deviceId);
        await cmd.ExecuteNonQueryAsync();
    }

    // MARK: live activity

    /// <summary>
    /// Records what the app reports about its Live Activity. Null leaves a value
    /// as stored; an empty activity token means the activity is gone. A new
    /// activity token clears the last fingerprint so the next evaluation pushes, and
    /// takes <paramref name="renewBy"/> as the deadline; without one, a deadline that
    /// has passed moves to <see cref="LFHub.Engine.HubEngine.LiveActivityLifetime"/> from now,
    /// since the new activity has just started.
    /// </summary>
    public async Task ReportLiveActivityAsync(Guid deviceId, string? activityToken, string? pushToStartToken,
        DateTime? renewBy)
    {
        await using var cmd = _ds.CreateCommand(
            """
            UPDATE lfhub.devices SET
                la_last_fingerprint = CASE WHEN @at IS NOT NULL AND @at IS DISTINCT FROM coalesce(la_token, '')
                                           THEN NULL ELSE la_last_fingerprint END,
                la_renewal_failed   = CASE WHEN @at IS NOT NULL AND @at <> '' AND @at IS DISTINCT FROM la_token
                                           THEN false ELSE la_renewal_failed END,
                la_token            = CASE WHEN @at IS NULL THEN la_token ELSE nullif(@at, '') END,
                la_push_to_start_token = coalesce(nullif(@pst, ''), la_push_to_start_token),
                la_renew_by         = CASE WHEN @at IS NOT NULL AND @at <> '' AND @at IS DISTINCT FROM la_token
                                           THEN coalesce(@rb, CASE WHEN la_renew_by <= now() THEN now() + @life
                                                                   ELSE la_renew_by END)
                                           ELSE la_renew_by END
            WHERE id = @id
            """);
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.Text) { Value = (object?)activityToken ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("pst", NpgsqlDbType.Text) { Value = (object?)pushToStartToken ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("rb", NpgsqlDbType.TimestampTz) { Value = (object?)renewBy ?? DBNull.Value });
        cmd.Parameters.AddWithValue("life", LFHub.Engine.HubEngine.LiveActivityLifetime);
        cmd.Parameters.AddWithValue("id", deviceId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RecordLiveActivityPushAsync(Guid deviceId, string? fingerprint, string status, bool delivered,
        bool tokenGone)
    {
        await using var cmd = _ds.CreateCommand(
            "UPDATE lfhub.devices SET la_last_push_at = now(), la_last_push_status = @s, " +
            "la_last_fingerprint = CASE WHEN @d THEN @f ELSE la_last_fingerprint END, " +
            "la_token = CASE WHEN @g THEN NULL ELSE la_token END WHERE id = @id");
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("d", delivered);
        cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Text) { Value = (object?)fingerprint ?? DBNull.Value });
        cmd.Parameters.AddWithValue("g", tokenGone);
        cmd.Parameters.AddWithValue("id", deviceId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Records a renewal attempt. The deadline and the old activity token stay until
    /// the app reports the new activity's token (<see cref="ReportLiveActivityAsync"/>);
    /// a failed attempt sets the flag that shows the renewal overlay.
    /// </summary>
    public async Task RecordLiveActivityRenewalAsync(Guid deviceId, bool started, string status,
        bool pushToStartTokenGone)
    {
        await using var cmd = _ds.CreateCommand(
            "UPDATE lfhub.devices SET la_last_push_at = now(), la_last_push_status = @s, " +
            "la_renewal_failed = NOT @st, " +
            "la_last_fingerprint = NULL, " +
            "la_push_to_start_token = CASE WHEN @pg THEN NULL ELSE la_push_to_start_token END WHERE id = @id");
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("st", started);
        cmd.Parameters.AddWithValue("pg", pushToStartTokenGone);
        cmd.Parameters.AddWithValue("id", deviceId);
        await cmd.ExecuteNonQueryAsync();
    }

    // MARK: client settings and alarms

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task SetClientSettingsAsync(Guid deviceId, JsonObject settings)
    {
        await using var cmd = _ds.CreateCommand("UPDATE lfhub.devices SET client_settings = @s WHERE id = @id");
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Jsonb) { Value = settings.ToJsonString() });
        cmd.Parameters.AddWithValue("id", deviceId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Stores the app's alarm export as uploaded (alarms plus configuration).
    /// Alarms the hub disabled and the export now shows disabled have reached
    /// the app, so they leave the hub's list. Moves the alarm revision, so an
    /// evaluation that read the earlier export does not write its state over this one.
    /// </summary>
    public async Task SetAlarmsAsync(Guid deviceId, string exportJson, IReadOnlyCollection<Guid> disabledInExport)
    {
        await using var cmd = _ds.CreateCommand(
            "UPDATE lfhub.devices SET alarms_json = @a, alarm_rev = alarm_rev + 1, hub_disabled_alarms = coalesce((" +
            "SELECT jsonb_agg(e) FROM jsonb_array_elements_text(hub_disabled_alarms) e WHERE NOT (e = ANY(@d))" +
            "), '[]') WHERE id = @id");
        cmd.Parameters.AddWithValue("a", exportJson);
        cmd.Parameters.AddWithValue("d", disabledInExport.Select(g => g.ToString()).ToArray());
        cmd.Parameters.AddWithValue("id", deviceId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Persists one evaluation: the engine's state, the alarm being delivered, if any, and the
    /// alarms the engine turned off (<paramref name="disable"/>, a Temporary alarm after it
    /// fired), which stay off until the app has them off too. Writes only while the alarm
    /// revision is still <paramref name="readRev"/>, the one the evaluation read; false when
    /// the app handled an alarm or uploaded its alarms in the meantime. A delivery receipt
    /// recorded on the same firing after the evaluation read it is kept.
    /// </summary>
    public async Task<bool> SaveAlarmEvaluationAsync(Guid deviceId, int readRev, string stateJson, ActiveAlarm? active,
        IReadOnlyCollection<Guid> disable)
    {
        await using var cmd = _ds.CreateCommand(
            """
            UPDATE lfhub.devices SET
                alarm_state  = @st,
                active_alarm = CASE WHEN @act->>'deliveredAt' IS NULL AND active_alarm->>'deliveredAt' IS NOT NULL
                                         AND @act->>'alarmId' = active_alarm->>'alarmId'
                                         AND @act->>'firedAt' = active_alarm->>'firedAt'
                                    THEN @act || jsonb_build_object('deliveredAt', active_alarm->'deliveredAt')
                                    ELSE @act END,
                hub_disabled_alarms = CASE WHEN cardinality(@dis) = 0 THEN hub_disabled_alarms
                    ELSE (SELECT jsonb_agg(DISTINCT e) FROM (
                        SELECT jsonb_array_elements_text(hub_disabled_alarms) e UNION SELECT unnest(@dis)) x) END
            WHERE id = @id AND alarm_rev = @rev
            """);
        cmd.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Jsonb) { Value = stateJson });
        cmd.Parameters.Add(new NpgsqlParameter("act", NpgsqlDbType.Jsonb)
        {
            Value = active is null ? DBNull.Value : JsonSerializer.Serialize(active, JsonOpts),
        });
        cmd.Parameters.Add(new NpgsqlParameter("dis", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = disable.Select(g => g.ToString()).ToArray(),
        });
        cmd.Parameters.AddWithValue("id", deviceId);
        cmd.Parameters.AddWithValue("rev", readRev);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    /// <summary>
    /// Records when the alarm push for one firing (<paramref name="alarmId"/> fired at
    /// <paramref name="firedAt"/>) went out and APNs' answer, on the device's sounding alarm.
    /// False when the device's sounding alarm is not that firing: the app handled it meanwhile.
    /// </summary>
    public async Task<bool> RecordAlarmPushAsync(Guid deviceId, Guid alarmId, DateTime firedAt, DateTime at,
        string status)
    {
        await using var cmd = _ds.CreateCommand(
            """
            UPDATE lfhub.devices SET
                active_alarm = active_alarm || jsonb_build_object('lastPushAt', @at, 'lastPushStatus', @s)
            WHERE id = @id AND active_alarm->>'alarmId' = @a AND active_alarm->>'firedAt' = @f
            """);
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(at, JsonOpts) });
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("id", deviceId);
        cmd.Parameters.AddWithValue("a", alarmId.ToString());
        cmd.Parameters.AddWithValue("f", JsonStamp(firedAt));
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    /// <summary>
    /// Keeps a delivery receipt token, by its hash, for the alarm push about to carry it, until
    /// <see cref="LFHub.Engine.AlarmReceipts.Lifetime"/> after <paramref name="at"/>. Removes the
    /// receipts that have expired.
    /// </summary>
    public async Task IssueReceiptAsync(string tokenHash, Guid deviceId, Guid alarmId, DateTime firedAt, DateTime at)
    {
        await using var batch = _ds.CreateBatch();
        var expired = new NpgsqlBatchCommand("DELETE FROM lfhub.alarm_receipts WHERE expires_at <= @now");
        expired.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = at });
        var insert = new NpgsqlBatchCommand(
            "INSERT INTO lfhub.alarm_receipts (token_hash, device_id, alarm_id, fired_at, expires_at) " +
            "VALUES (@h, @d, @a, @f, @exp)");
        insert.Parameters.AddWithValue("h", tokenHash);
        insert.Parameters.AddWithValue("d", deviceId);
        insert.Parameters.AddWithValue("a", alarmId);
        insert.Parameters.AddWithValue("f", JsonStamp(firedAt));
        insert.Parameters.Add(new NpgsqlParameter("exp", NpgsqlDbType.TimestampTz)
        {
            Value = at + LFHub.Engine.AlarmReceipts.Lifetime,
        });
        batch.BatchCommands.Add(expired);
        batch.BatchCommands.Add(insert);
        await batch.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Takes a delivery receipt: the token is used up, and when the firing it was sent for is
    /// still the device's sounding alarm and has no receipt yet, <paramref name="at"/> becomes
    /// its <c>deliveredAt</c>. Null for a token that is unknown, used or expired.
    /// </summary>
    public async Task<(Guid DeviceId, Guid AlarmId, bool Recorded)?> RedeemReceiptAsync(string tokenHash, DateTime at)
    {
        await using var cmd = _ds.CreateCommand(
            """
            WITH receipt AS (
                DELETE FROM lfhub.alarm_receipts WHERE token_hash = @h AND expires_at > @now
                RETURNING device_id, alarm_id, fired_at),
            delivered AS (
                UPDATE lfhub.devices d SET active_alarm = d.active_alarm || jsonb_build_object('deliveredAt', @at)
                FROM receipt r
                WHERE d.id = r.device_id AND d.active_alarm->>'alarmId' = r.alarm_id::text
                  AND d.active_alarm->>'firedAt' = r.fired_at AND d.active_alarm->>'deliveredAt' IS NULL
                RETURNING d.id)
            SELECT device_id, alarm_id, EXISTS (SELECT 1 FROM delivered) FROM receipt
            """);
        cmd.Parameters.AddWithValue("h", tokenHash);
        cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = at });
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(at, JsonOpts) });
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? (r.GetGuid(0), r.GetGuid(1), r.GetBoolean(2)) : null;
    }

    /// A time as the JSON of <see cref="ActiveAlarm"/> holds it, without the quotes.
    private static string JsonStamp(DateTime at) => JsonSerializer.Serialize(at, JsonOpts).Trim('"');

    public async Task DeleteDeviceAsync(Guid deviceId)
    {
        await using var cmd = _ds.CreateCommand("DELETE FROM lfhub.devices WHERE id = @id");
        cmd.Parameters.AddWithValue("id", deviceId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Registers the device for APNs with the key of an admin device of the patient that pushes
    /// for the same team and bundle id; false when there is none.
    /// </summary>
    public async Task<bool> SetSharedApnsAsync(Guid deviceId, string token, string env, string bundleId,
        string teamId)
    {
        await using var cmd = _ds.CreateCommand(
            "UPDATE lfhub.devices d SET apns_token = @t, apns_env = @e, apns_bundle_id = @b, apns_team_id = @tm, " +
            "apns_key_id = a.apns_key_id, apns_key_sealed = a.apns_key_sealed, apns_key_shared = true, " +
            "last_badge = NULL, last_push_status = NULL, push_token_gone = false " +
            "FROM (SELECT apns_key_id, apns_key_sealed FROM lfhub.devices WHERE role = 'admin' AND NOT apns_key_shared " +
            "AND apns_key_sealed IS NOT NULL AND apns_team_id = @tm AND apns_bundle_id = @b " +
            "AND patient_id = (SELECT patient_id FROM lfhub.devices WHERE id = @id) " +
            "ORDER BY last_seen_at DESC NULLS LAST LIMIT 1) a WHERE d.id = @id");
        cmd.Parameters.AddWithValue("t", token);
        cmd.Parameters.AddWithValue("e", env);
        cmd.Parameters.AddWithValue("b", bundleId);
        cmd.Parameters.AddWithValue("tm", teamId);
        cmd.Parameters.AddWithValue("id", deviceId);
        if (await cmd.ExecuteNonQueryAsync() == 0) return false;

        await using var dedupe = _ds.CreateCommand(
            "DELETE FROM lfhub.devices WHERE id <> @id AND apns_token = @t AND apns_bundle_id = @b");
        dedupe.Parameters.AddWithValue("id", deviceId);
        dedupe.Parameters.AddWithValue("t", token);
        dedupe.Parameters.AddWithValue("b", bundleId);
        await dedupe.ExecuteNonQueryAsync();
        return true;
    }

    public enum RoleChange { Changed, UnknownDevice, LastAdmin }

    /// <summary>Sets a device's role; the patient always keeps one admin.</summary>
    public async Task<RoleChange> SetRoleAsync(Guid patientId, Guid deviceId, string role)
    {
        await using var cmd = _ds.CreateCommand(
            "UPDATE lfhub.devices SET role = @r WHERE id = @id AND patient_id = @p AND (@r = 'admin' OR EXISTS (" +
            "SELECT 1 FROM lfhub.devices o WHERE o.patient_id = @p AND o.id <> @id AND o.role = 'admin'))");
        cmd.Parameters.AddWithValue("r", role);
        cmd.Parameters.AddWithValue("id", deviceId);
        cmd.Parameters.AddWithValue("p", patientId);
        if (await cmd.ExecuteNonQueryAsync() == 1) return RoleChange.Changed;
        return await DeviceAsync(deviceId) is { } d && d.PatientId == patientId ? RoleChange.LastAdmin : RoleChange.UnknownDevice;
    }

    // MARK: invites

    /// <summary>Stores an invite code by its hash; it works once, until <paramref name="expiresAt"/>.</summary>
    public async Task CreateInviteAsync(Guid patientId, Guid createdBy, string code, DateTime expiresAt)
    {
        await using var cmd = _ds.CreateCommand(
            "DELETE FROM lfhub.invites WHERE expires_at <= now(); " +
            "INSERT INTO lfhub.invites (code_hash, patient_id, created_by, expires_at) VALUES (@h, @p, @c, @x)");
        cmd.Parameters.AddWithValue("h", Hash(code));
        cmd.Parameters.AddWithValue("p", patientId);
        cmd.Parameters.AddWithValue("c", createdBy);
        cmd.Parameters.AddWithValue("x", expiresAt);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The patient an unused, unexpired invite code is for; null for any other code.</summary>
    public async Task<Guid?> InvitePatientAsync(string code, DateTime now)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT patient_id FROM lfhub.invites WHERE code_hash = @h AND expires_at > @now");
        cmd.Parameters.AddWithValue("h", Hash(code));
        cmd.Parameters.AddWithValue("now", now);
        return await cmd.ExecuteScalarAsync() is Guid id ? id : null;
    }

    /// <summary>Uses an invite code up; false when it is unknown, used or expired.</summary>
    public async Task<bool> UseInviteAsync(string code, DateTime now)
    {
        await using var cmd = _ds.CreateCommand(
            "DELETE FROM lfhub.invites WHERE code_hash = @h AND expires_at > @now");
        cmd.Parameters.AddWithValue("h", Hash(code));
        cmd.Parameters.AddWithValue("now", now);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    /// <summary>Removes one of the patient's devices; false when the patient has no such device.</summary>
    public async Task<bool> DeletePatientDeviceAsync(Guid patientId, Guid deviceId)
    {
        await using var cmd = _ds.CreateCommand("DELETE FROM lfhub.devices WHERE id = @id AND patient_id = @p");
        cmd.Parameters.AddWithValue("id", deviceId);
        cmd.Parameters.AddWithValue("p", patientId);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    private Device ReadDevice(NpgsqlDataReader r)
    {
        ApnsConfig? apns = null;
        var registered = !r.IsDBNull(4) && !r.IsDBNull(9);
        if (registered && OpenOrNull(r.GetString(9), "APNs key", r.GetGuid(0)) is { } key)
            apns = new ApnsConfig(r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
                r.GetString(8), key);

        return new Device
        {
            Id = r.GetGuid(0),
            PatientId = r.GetGuid(1),
            Name = r.GetString(2),
            Settings = JsonNode.Parse(r.GetString(3))!.AsObject(),
            Apns = apns,
            ApnsKeyDoesNotOpen = registered && apns is null,
            LastBadge = r.IsDBNull(10) ? null : r.GetInt32(10),
            LastPushAt = r.IsDBNull(11) ? null : r.GetDateTime(11),
            LastPushStatus = r.IsDBNull(12) ? null : r.GetString(12),
            LastSeenAt = r.IsDBNull(13) ? null : r.GetDateTime(13),
            LiveActivity = new LiveActivityState
            {
                Token = r.IsDBNull(14) ? null : r.GetString(14),
                PushToStartToken = r.IsDBNull(15) ? null : r.GetString(15),
                RenewBy = r.IsDBNull(16) ? null : r.GetDateTime(16),
                RenewalFailed = r.GetBoolean(17),
                LastFingerprint = r.IsDBNull(18) ? null : r.GetString(18),
                LastPushAt = r.IsDBNull(19) ? null : r.GetDateTime(19),
                LastPushStatus = r.IsDBNull(20) ? null : r.GetString(20),
            },
            ClientSettings = JsonNode.Parse(r.GetString(21))!.AsObject(),
            AlarmsJson = r.IsDBNull(22) ? null : r.GetString(22),
            AlarmStateJson = r.IsDBNull(23) ? null : r.GetString(23),
            ActiveAlarm = r.IsDBNull(24) ? null : JsonSerializer.Deserialize<ActiveAlarm>(r.GetString(24), JsonOpts),
            HubDisabledAlarms = JsonSerializer.Deserialize<List<Guid>>(r.GetString(25)) ?? [],
            AlarmRev = r.GetInt32(26),
            LastPushOkAt = r.IsDBNull(27) ? null : r.GetDateTime(27),
            PushFailingSince = r.IsDBNull(28) ? null : r.GetDateTime(28),
            LastAlarmPushOk = r.IsDBNull(29) ? null : r.GetBoolean(29),
            CreatedAt = r.GetDateTime(30),
            Role = r.GetString(31),
            ApnsKeyShared = r.GetBoolean(32),
        };
    }
}
