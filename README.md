# LF Hub

A server for LoopFollow, centred on the patient. It follows the patient's
Nightscout (REST on LoopFollow's cadence, with the Socket.IO stream as an early
trigger) and Dexcom Share, and does work on behalf of each registered
LoopFollow app over APNs: the app badge, the Live Activity, alarms, and a
bundle of the data the app would otherwise fetch itself.

API only, no web UI. The app does everything: it claims the hub, configures the
patient's data sources and turns features on.

LF Hub is open-source software that people build and run themselves. It is not
a medical device and gives no dosing advice. Its alarms depend on the hub, the
network, Apple's push service and the phone, any of which can fail; it should
not be the only way an alarm reaches someone. Talk to your care team about how
you use alarms.

## Model

- **Patient**: its data sources — Nightscout (URL + token), Dexcom Share, or both. A hub
  serves one family and one patient; the database holds at most one (index
  `patients_one_per_hub`). A host offering hubs runs one instance per family, as with
  Nightscout itself.
- **Device**: one LoopFollow installation, bound to one patient, with a role, its own bearer key, its APNs registration (token, team,
  bundle id, key id, .p8), its hub `settings`, its LoopFollow client settings,
  its uploaded alarm list, and the alarm state the hub keeps for it. An **admin** device
  sets the patient's sources, removes devices, changes roles and invites; a **member**
  device follows, with its own alarms. The setup token makes an admin, an invite a member,
  and the patient always keeps one admin.

A device carries its own .p8 because apps following one patient can be built
under different Apple teams.

## API

All `/v1` calls except claim and receipt take `Authorization: Bearer <deviceKey>`
and answer 401 without a valid key. Other errors are `application/problem+json`.
A request body is at most 256 KB; a larger one is a 413.

| | |
|---|---|
| `POST /v1/claim` | `{setupToken, patientName?, deviceName?, replace?, copyFrom?}` → `{deviceId, deviceKey, patientId, role}`, always an admin. Creates the patient if none exists. Without `replace` or `copyFrom` it adds a new device. `replace` (a device id) hands that device to the calling phone, as on a phone change: the device keeps its settings, client settings, alarms and alarm state and gets a new key, the old key stops working, and its push and Live Activity registration is cleared until the new phone sends its own. `copyFrom` (a device id) adds a new device whose settings, client settings and alarm export are copies of that device's. The app's version and features are not carried over. 404 for an unknown device; 400 for both at once; 403 for a wrong setup token; 429 (with `Retry-After`) after five claims or device listings in a minute, right or wrong. |
| `POST /v1/claim/devices` | `{setupToken}` → `{devices: [{id, name, role, createdAt, lastSeenAt, appVersion}]}`, the devices a joining phone can replace or copy; empty on an unclaimed hub. Shares the claim limit. |
| `POST /v1/invites` | Admin only. → `{code, expiresAt}`: a code that lets one phone join as a member, once, within 24 hours. The hub keeps only its SHA-256. The app shows it as a QR code of `<hub URL>/join#<code>`. |
| `POST /v1/join/devices` | `{code}` → the devices, as for `POST /v1/claim/devices`. 403 for an unknown, used or expired code. Shares the claim limit. |
| `POST /v1/join` | `{code, deviceName?, replace?, copyFrom?}` → `{deviceId, deviceKey, patientId, role: "member"}`, as `POST /v1/claim` but as a member, and `replace` takes only a member device (403 for an admin device). Uses the code up. Shares the claim limit. |
| `GET /join` | A plain page for a camera that scanned an invite: open LoopFollow and scan it there. |
| `GET /v1/me` | The device (settings, APNs state, last push, push health, alarm list state, active alarm, alarms the hub disabled, Live Activity state), the patient (name, Nightscout host, whether Dexcom is set, whether it is followed and has data) and the live stream status. Never returns keys, tokens or passwords. See below. |
| `PUT /v1/device/apns` | `{token, environment: production\|sandbox, bundleId, teamId, keyId?, key?}`. `key` (the .p8) is kept when left out. Without `keyId`, or without `key` on first registration, the device pushes with the key of an admin device of the patient registered for the same team and bundle id (`apnsKeyShared` in `/v1/me`), and follows that device when it sends a new key; with no such device it is a 400. A device that sends its own key stops sharing. `token` must be hex, `bundleId` letters, digits, `.` and `-`, `teamId` and `keyId` ten upper-case letters or digits, and `key` a P-256 private key in PKCS #8 (Apple's .p8, with or without its PEM lines); anything else is a 400 and nothing is stored. Registering a push token removes any other device with the same token and bundle id, so a reinstalled app does not leave a second device alarming. |
| `PUT /v1/device/settings` | Merge of `{badge: bool, liveActivity: bool, alarms: bool, criticalAlerts: bool, unit: mgdl\|mmol, appVersion: string, features: [string]}` (`appVersion` and each feature at most 32 characters, at most 32 features; see Compatibility); any other key, or a value of another type, is a 400. `alarms: true` is a 409 while the device has not reported every feature the hub requires (`receipts`). `alarms: true` is a 409 while the device has no alarm list that parses (upload it with `PUT /v1/device/alarms` first), or no client settings whose `timeZone` the hub knows (upload them with `PUT /v1/device/clientsettings` first), since day and night are the phone's. Answers the merged settings. |
| `PUT /v1/device/clientsettings` | The LoopFollow settings that change what the hub builds, keyed as LoopFollow stores them (`units`, `predictionToLoad`, `downloadPrediction`, `downloadTreatments`, `downloadDays`, `showYesterdayLine` (the BG history then reaches one day further back, for the graph's yesterday overlay), `bgUpdateDelay`, `infoDisplayItems`, `persistentNotification`, and Speak BG's `speakBG`, `speakBGAlways`, `speakLowBG`, `speakProactiveLowBG`, `speakHighBG`, `speakFastDropDelta`, `speakLowBGLimit`, `speakHighBGLimit`, `speakLanguage`), plus the phone's `timeZone` (an IANA zone such as `Europe/Stockholm`), `decimalSeparator` and `timeFormat`. Missing or mistyped keys take LoopFollow's defaults; without a `timeZone` the hub knows, day and night and local times are taken in UTC, which the hub logs once per device that names a zone or has alarms on. |
| `PUT /v1/device/alarms` | The app's alarm export (`AlarmSettingsExport` JSON) as is: alarms plus the global alarm configuration. Snoozes, Snooze All and Mute travel this way. An alarm of a type or option the hub does not know is skipped and logged; the others keep working. An export that does not parse at all, or that holds more than 200 alarms, is a 400. The hub parses each export once, when it first checks it. |
| `POST /v1/receipt/{token}` | No body and no device key: the token is the credential. The phone's delivery receipt for an alarm push, `token` being the push's `lfhubReceipt`. 204 when the token is known, unused and less than 30 minutes old, which uses it up; 404 for any other token; 429 (with `Retry-After`) beyond 120 receipts a minute across the hub. See Alarms. |
| `POST /v1/device/alarm/{alarmId}/handled` | The app snoozed or acknowledged the alarm, by any of its snooze paths. See Alarms. |
| `PUT /v1/device/liveactivity` | `{activityToken?, pushToStartToken?, renewBy?}` as the app knows them. A null field is left as stored; an empty `activityToken` means the activity is gone. `renewBy` (Unix seconds) is taken only together with a new activity token. |
| `POST /v1/device/testalarm` | `{title?, sound?, delaySeconds?}`. Sends one alarm push to the calling device, outside the alarm engine, so the user can check that an alarm rings through silent mode. With `delaySeconds` (at most 120) it answers 202 at once and sends later, so the phone can be locked first; otherwise 204, or 502 with APNs' answer. 409 without an APNs registration. One a minute per device; 429 (with `Retry-After`) otherwise. |
| `GET /v1/device/log?hours=` | The hub's recent log (default 48 hours, 1–72), for LoopFollow's Share Logs. The hub serves one family, so any of its devices reads the whole log. |
| `GET /v1/patient/bundle` | The patient's bundle. See Bundle. |
| `DELETE /v1/device` | Unregisters the calling device. |
| `GET /v1/devices` | `{devices: [{id, name, createdAt, lastSeenAt, appVersion, lastPushOkAt, pushFailingSince, current}]}`, the patient's devices; `current` marks the calling one. |
| `DELETE /v1/devices/{id}` | Admin only. Removes one of the patient's devices; 404 for an unknown one. |
| `PUT /v1/devices/{id}/role` | Admin only. `{role: admin\|member}`; 409 when it would leave the patient without an admin. |
| `GET /v1/device/alarms`, `GET /v1/device/clientsettings` | The alarm export and client settings as last uploaded, so a phone that replaced or copied a device can start from them; 404 when none was uploaded. |
| `GET /v1/patient/source` | `{nightscoutUrl, token, dexcom: {username, password, server}}`, the patient's sources in full (null when unset), so every phone can follow the patient by itself when the hub does not answer. Each read is logged. |
| `PUT /v1/patient/source` | Admin only. `{nightscoutUrl?, token?, dexcom?: {username, password, server}}`. Sets the patient's sources and moves their revision (`sourceRevision` in `/v1/me`), so every phone fetches them again; one left out is removed. At least one is required. The Nightscout URL must be https, and its host must not resolve to a loopback, private, shared (100.64.0.0/10), link-local or unique-local address; any other URL is a 400. With `LFHUB_ALLOW_PRIVATE_NIGHTSCOUT=true` such hosts are accepted, and so is plain http for a loopback host (`localhost`, `127.0.0.1`, `::1`), for a local test setup. With Dexcom, BG comes from Share first and from Nightscout when Share is stale (as in LoopFollow); a Dexcom login that Share rejects for its account or password (an unknown account, a wrong password, too many attempts) blocks further logins for 15 minutes, since Share locks accounts after repeated bad logins; a login that fails otherwise (Share erring or out of reach) is tried again at the next fetch. |
| `GET /healthz` | 200 while the process serves requests. No dependencies. |
| `GET /livez` | Liveness for the orchestrator: 503 `database` when Postgres does not answer within 3 s, 503 `engine` when the engine loop has not finished a tick for 2 minutes, otherwise 200. A Nightscout outage does not fail it, since a restart would not help. |
| `GET\|HEAD /status` | Health for external monitors (UptimeRobot): 200 or 503, `Cache-Control: no-store`. Fails on the database, a stored secret that does not open under `LFHUB_MASTER_KEY` (`secrets`), a stalled engine, or a Nightscout socket not authorized for 2 minutes, however many reconnect attempts it made. Without a key the body is `{"status":"ok"\|"fail"}` only. With `?key=<LFHUB_STATUS_KEY>` it adds `problems, streams, connected, fresh`, and `&fresh=1` also fails when the latest reading is 12 minutes old or more — whether the patient's data is fresh is for the key holder only. |

`GET /v1/me` answers, besides the fields above:

```
{
  "device": {
    "lastPushOkAt": "2026-10-24T12:00:00Z" | null,
    "pushFailingSince": "2026-10-24T12:01:00Z" | null,
    "alarmsReceived": true,
    "alarmCount": 2,
    "lastAlarmPushOk": true | false | null,
    "activeAlarm": {
      "alarmId": "…", "title": "…", "firedAt": "…",
      "lastPushAt": "…", "lastPushStatus": "200" | null,
      "deliveredAt": "2026-10-24T12:00:04Z" | null
    } | null,
    ...
  },
  "patient": { "followed": true, "hasData": true, ... },
  ...
}
```

- `lastPushAt`, `lastPushStatus`: when the last badge push went out and APNs'
  answer to it. Only badge pushes set them; every push counts in `lastPushOkAt`
  and `pushFailingSince`.
- `lastPushOkAt`: when a push to the device's own push token (badge, alarm,
  alarm stop, Latest BG, speech, heartbeat, test alarm) was last accepted by APNs. Pushes
  to a Live Activity token are not counted: a rejected one means the activity ended.
- `pushFailingSince`: when the pushes to the device began to be rejected; null
  while the last one was accepted. Pushes rejected for 10 minutes are logged as a
  warning, once per outage.
- `alarmsReceived`: the device has uploaded an alarm list that parses;
  `alarmCount` is the number of alarms in it (0 without one).
- `lastAlarmPushOk`: whether the last alarm push (an alarm or a test alarm) was
  accepted; null before the first.
- `activeAlarm`: the alarm sounding on the device, if any: when it fired, when its
  push was last sent and APNs' answer (null until known), and `deliveredAt`, when
  the phone's first delivery receipt for it arrived (null until then).
- `followed`: the engine follows the patient; `hasData`: the hub has a latest
  reading for it.

## Compatibility

The app and the hub are updated independently, so each must work with an older or newer
other side. The API is versioned in its path; within `/v1` changes are additive: new keys
and fields are optional, and each side ignores what it does not know. A change that cannot
be made that way becomes `/v2`, served next to `/v1` for a transition period.

Each side reports what it supports. The app sends `appVersion` and `features` with
`PUT /v1/device/settings`; `GET /v1/me` answers with
`hub: {version, api, features}`. A feature is used only once both sides have reported it:

| Feature | Meaning |
|---|---|
| `receipts` | The app posts a delivery receipt for every alarm push. Required: `alarms: true` is refused (409, "update LoopFollow") until the app reports it. |
| `speak` | The app's notification service extension speaks Speak BG pushes; the hub sends them only to apps that report it. |
| `heartbeat`, `bundle`, `latestBg` | Provided by the hub. |
| `devices` | Joining in a device's place or as its copy, the device list, and reading back the sources, alarms and client settings. |
| `roles`, `invites` | Admin and member devices, and joining with an invite. |
| `sourceRevision` | `patient.sourceRevision` in `/v1/me`. |
| `sharedApnsKey` | A device may register for APNs without a key of its own. |

A mismatch always ends with the app alarming by itself: the app confirms a hub only when
`api` is 1 and the hub reports every feature the app requires, and a hub that refuses
alarms leaves the app unconfirmed. Database migrations add tables, columns and indexes and
never remove or rename them, so an older hub runs on a newer schema.

## Security

- **Device keys** are `lfh_` plus 32 random bytes (base64url). The hub returns a
  key once, at claim, and stores only its SHA-256.
- **Secrets at rest** — the Nightscout token, the Dexcom account and each
  device's APNs .p8 — are sealed with AES-256-GCM under `LFHUB_MASTER_KEY`, with
  a fresh random nonce per value. A value that does not open under the current
  key (the key was lost or changed) is treated as unset and logged as an error,
  once per value while the process runs; `/status` fails with `secrets` until
  every stored secret opens, and a device with alarms on whose APNs key does not
  open is logged once as getting no alarms. The hub keeps serving, and an app sets
  the secret again with the call that set it (`PUT /v1/device/apns`,
  `PUT /v1/patient/source`).
- **The setup token** (`LFHUB_SETUP_TOKEN`) is the hub's root credential: whoever
  has it can add an admin device to the patient, take over any device and read the
  patient's sources. It lives only in the hub's
  environment and is compared in constant time. Claims are limited to five a
  minute across the hub.
- **Invites** are single-use codes of 20 random bytes, valid for 24 hours, stored as
  their SHA-256 and limited with the claims. A member reads the patient's sources, since
  its phone follows the patient by itself when the hub does not answer; the roles keep a
  member from changing them, not from reading them.
- **Devices** are removed when they have not called the hub for 30 days and
  APNs rejected the last push to their own push token as gone (410,
  `Unregistered` or `BadDeviceToken`): the app was deleted, or the phone reset.
  A device whose pushes are still accepted is kept however long its app stays
  closed, so its alarms keep coming. Registering a push token again clears the
  rejection. A rejected Live Activity token does not count. A push token
  belongs to one device per app (see `PUT /v1/device/apns`).
- **Delivery receipt tokens** (`lfhubReceipt`) are bearer capabilities: whoever
  holds one can mark that one alarm push as received, which ends the minute-by-minute
  delivery of an alarm whose sound does not repeat. Each is 128 random bits, sent
  only inside the alarm push to the device's own push token, good for one use within
  30 minutes, kept only as its SHA-256 with the device and alarm it was sent for,
  and never logged. `POST /v1/receipt` takes at most 120 a minute across the hub.
- **Sources** are fetched from public addresses only (see `PUT /v1/patient/source`),
  checked when the URL is set and again on every connection, REST and socket alike,
  so a name that later resolves into the hub's own network is refused too. A host
  offering hubs can thus not be probed through a hub's Nightscout URL.
- **Logs** carry device and patient ids, alarm ids and types, and push
  statuses, never keys, tokens, passwords or glucose values. `/status` without its key says ok or fail only.
- The hub speaks plain HTTP on port 8080; TLS is terminated in front of it.

## Readings

The hub reads Nightscout and Dexcom as LoopFollow does, with one difference: a
reading that comes less than 30 seconds after the one before it is taken as the
same reading from a second uploader (Trio and a Dexcom bridge write each reading
about 10 seconds apart, and may round it differently) and dropped, whatever its
value, keeping the earliest copy. LoopFollow drops only a repeat of the same value
and keeps the later copy. Readings from a 1-minute CGM stay apart. Nightscout itself
is never changed. When the two uploaders disagree on the value (a raw and a
calibrated reading, or a different rounding), the hub's alarms and the bundle the
app shows use the value of the uploader that wrote first, and that value decides
whether an alarm at its limit fires.

A reading, pump clock or loop time more than 5 minutes ahead of the hub's clock comes
from a clock that is wrong and is dropped (a warning is logged once per newest
dropped reading); taken as the latest, it would hold off the BG and loop alarms until
real time caught up. `downloadDays` is taken up to 4, the most LoopFollow offers.

## Badge

On every new reading, and on a 30-second tick, each device with `badge: true`
gets the value LoopFollow itself would show: mg/dL as is, mmol/L to one
decimal without the separator. A reading older than 12 minutes gives badge 0.
A push is sent only when the value changes. A failed push is retried after
5 minutes.

## Alarms

For each device with `alarms: true` and an uploaded alarm list, the hub runs
LoopFollow's own alarm check (`AlarmManager.checkAlarms`, ported with each
condition) against the AlarmData LoopFollow would build from the same data and
the device's client settings: at most one alarm per check, in LoopFollow's
priority order; BG alarms at most once per reading and only on a reading at most
5 minutes old; snoozes, Snooze All, Mute, and day/night windows in the phone's
time zone. Checks run once a minute, and at once on new data, on an alarm upload
and on `handled`. BG and devicestatus are fetched at least every 270 seconds, so
the missed-reading and not-looping alarms, which stay silent without a check in
the last 6 minutes, keep working when readings stop.

When an alarm fires, the device gets a time-sensitive push (a critical alert once
`criticalAlerts` is on) with LoopFollow's title, subtitle and body, the alarm's
sound as the app's converted file `lfhub-<sound>.caf`, category `category`
(Snooze) or `lfhub-ack` (Acknowledge, for alarms whose snooze duration is 0),
and `mutable-content`, so the app's notification service extension can also ring
it as a system alarm (AlarmKit). A push not delivered within 5 minutes expires;
repeats of one alarm replace each other on the phone.

Every alarm push, repeats and test alarms included, carries `lfhubReceipt`: a
fresh token of 32 lower-case hex characters, which the app posts back to
`POST /v1/receipt/{token}` when the push arrives. The first receipt for the
sounding alarm becomes its `deliveredAt`, kept in the database. A receipt for a
push of an earlier firing, or of a test alarm, uses up its token and records nothing.

- **Repeat**: an alarm whose sound repeats is delivered again on every
  minute's check until it is silenced, receipts or not. Any other alarm is
  delivered again on every minute's check until a receipt for one of its pushes
  arrives, for as long as it sounds: its condition holds and it is neither
  handled nor silenced.
- **Silenced** means: the app uploads it snoozed or disabled, or without it, Snooze
  All begins, or the app calls `handled`. `handled` clears the sounding alarm and the
  engine's current alarm, so an acknowledged alarm (which is not snoozed) stops
  repeating and fires again on a later reading if its condition still holds. For an
  upload that silences an alarm ringing as a system alarm, the next check also sends
  the stop push (`lfhubStopAlarmId`) with the alarm's own text, since not every one
  of the app's snooze paths stops the system alarm itself; stopping one that has
  already stopped does nothing. When `handled` arrives while a push for that alarm
  is on its way, the stop push follows that push.
- **Another alarm fires** while one sounds: LoopFollow sounds one alarm at a time, so
  the hub stops the sounding one's system alarm with a stop push carrying its own text.
- **Stop pushes** go at priority 10. One APNs does not accept is sent again at each of
  the device's checks for 15 minutes, until it is accepted or the alarm sounds again.
- **Alarms turned off** (`alarms: false`) while an alarm sounds: the next check
  sends a stop push with the alarm's own text and clears the sounding and the
  current alarm.
- **One check, one write**: a check reads the device again right before it runs and
  writes its outcome (the engine's state, the sounding alarm, the alarms it turned
  off) before it sends anything, and only while the app has neither handled an
  alarm nor uploaded its alarms since that read. Otherwise it sends nothing, and
  the check that the app's call starts runs from what the app left.
- **Condition clears**: once the alarm has rung for a minute, received or not, the hub sends a
  passive stop push (`lfhubStopAlarmId`) with the alarm's title and the subtitle
  "Cleared", which tells the extension to stop the system alarm. Auto-snooze on
  CGM start stops a ringing alarm the same way.
- **Sound that does not repeat**: LoopFollow plays it once. The alarm push carries
  `lfhubRingOnce: 1`, and the extension stops the system alarm once the sound has
  played (at most about 25 seconds, the time an extension gets). The hub also stops the
  system alarm at the first check at least 30 seconds after the alarm's receipt
  arrived, with a passive stop push that carries the alarm's own title, subtitle
  and body, so Notification Center keeps the alarm's text. Until a receipt arrives
  the alarm counts as not rung: it is delivered every minute and stopped only when
  its condition clears or it is silenced. An app that posts no receipts therefore
  gets such an alarm every minute until it is handled or its condition clears.
- **Mute All**, or a play-sound option that rules out sound at this time: the
  notification is still delivered, with the default sound and without the system
  alarm, and delivered again only until its receipt arrives.
- A **Temporary** alarm turns itself off when it fires. The hub keeps it off
  (`disabledAlarmIds` in `/v1/me`) until the app's export shows it off too. As
  in LoopFollow, its sound goes on until it is handled, snoozed or Snooze All
  begins: being off does not silence it, and it repeats like any other alarm.
- With `persistentNotification`, a "Latest BG" notification goes out once per
  new reading when no alarm fired.

Not everything in the app's alarm settings applies to the hub's alarms:

- "Play sound during calls", the forced output volume and the volume-button snooze
  govern the app's own playback; a hub alarm sounds as its notification and system
  alarm do. Repeats come every minute, whatever the alarm's sound delay. "Ignore zero
  BG" is kept by LoopFollow, but no alarm reads it, in the app or the hub.
- An alarm limited to Day or Night uses LoopFollow's guard (`AlarmCondition.swift`)
  as it is: when Night Start is earlier in the day than Day Start, every time counts
  as night, so a Day-only alarm never fires and a Night-only alarm fires at any hour.
  The day/night choice of sound and repeat handles such a night as intended.

**Speak BG**: with `speakBG` on, each device with `alarms: true` and APNs registered
gets a speak push for a new reading that meets LoopFollow's speak conditions
(`SpeakBG.evaluateSpeakConditions`), and the app's notification service extension
speaks it. A reading is spoken at most once per device, only when it is at most 5
minutes old and has a reading before it. The conditions, in this order, compare the
new reading and the one before it in mg/dL, with the limits cut to whole mg/dL:

- **Always** (`speakBGAlways`, on by default): every new reading.
- **Low** (`speakLowBG`): the reading or the one before it is at or below
  `speakLowBGLimit` (default 72).
- **Proactive Low** (`speakProactiveLowBG`): as Low; or the first value of the
  forecast the alarms use (Loop's forecast, or the lowest of Trio's) is at or below
  `speakLowBGLimit`; or the reading is at or below `speakHighBGLimit` and at least
  `speakFastDropDelta` (default 10) below the one before it.
- **High** (`speakHighBG`): the reading or the one before it is at or above
  `speakHighBGLimit` (default 180).

The push is
`{"aps":{"alert":{"title":<Latest BG text>,"body":""},"sound":"default","interruption-level":"active","thread-id":"lfhub-speak","mutable-content":1},"lfhubSpeak":{"current":<mg/dL>,"previous":<mg/dL>,"language":<speakLanguage>,"mmol":<bool>}}`,
where the title is the subtitle of the "Latest BG" notification (reading, delta and
trend), with `apns-push-type: alert`, `apns-priority: 10`, `apns-collapse-id:
lfhub-speak` and an expiration 5 minutes ahead, since a late speech is of no use. A
push that fails is not sent again. The newest reading each device has had is kept in
memory; a device seen for the first time since the hub started, or whose speech was
off, starts from the current reading without speaking it, so a restarted hub repeats
no reading.

**Heartbeat**: each device with `alarms: true` and APNs registered gets a
background push at its first check and then every 20 minutes:
`{"aps":{"content-available":1},"lfhubHeartbeat":<Unix seconds>}`, with
`apns-push-type: background`, `apns-priority: 10`, `apns-collapse-id: lfhub-heartbeat` and an expiration 20 minutes
ahead. The app logs how long each one took to arrive; iOS may hold a background push
back or drop it, so these delays decide whether the heartbeat can drive a dead-man
alarm on the phone. The time of the last heartbeat is kept in memory, so a restarted
hub sends one at its first check. A rejected heartbeat counts toward the device's
push health like any other push.

The alarm state (the engine's state and the alarm being delivered) is stored per
device, written before a check sends its pushes. On shutdown, an alarm check that
has begun runs to its end, for at most 10 seconds, so the outcome of its push is
recorded as well, and no new check begins; the restarted hub then neither forgets
a sounding alarm nor fires one twice. A hub stopped harder than that (killed, or a
check that overran the 10 seconds) can leave a push without its recorded outcome:
the restarted hub sends that alarm again a minute after the push unless its
receipt has arrived, as for a push that failed, and does not fire it anew.

## Bundle

The hub keeps the latest response of every endpoint LoopFollow polls, fetched
on LoopFollow's cadence with the Nightscout socket (and, for Dexcom, the tick)
making parts due at once. In Hub mode the app reads these parts, the same for
every device, and long-polls for the next version while it is open.

`GET /v1/patient/bundle` answers `{version, generatedAt, parts: {entries,
profile, devicestatus, treatments, sage, cage, iage, dbsize: {fetchedAt,
data}}}`, each part the raw response LoopFollow would fetch itself (entries is
the merged Dexcom/Nightscout BG in entries.json shape; a part appears once it
has been fetched). `?since=<version>` answers 304 while unchanged; adding
`&wait=<s>` (at most 30) holds the request until the bundle changes. 503 until
the patient is followed.

## Live Activity

The app starts the activity and reports its push token, the push-to-start token
and the renewal deadline. While `liveActivity` is on, the hub sends the content
state LoopFollow itself would send (same keys, `reason: "hub"`, Unix seconds as
`seq`) whenever the snapshot changes, and at least every 5 minutes. Each update
carries a `stale-date` 15 minutes ahead, so the Lock Screen shows when the hub
has stopped sending. At the deadline (7.5 hours after start) the hub starts a
new activity through push-to-start; the old activity keeps updating until the
app reports the new one's token. Until then the deadline stays, and the renewal
is retried 30 minutes after each attempt. A new activity token takes the
`renewBy` sent with it; without one, a deadline that has passed moves 7.5 hours
ahead. A failed renewal also sets `showRenewalOverlay`. A failed update is retried after 5
minutes; a token APNs reports gone is forgotten.

## Configuration

| Variable | |
|---|---|
| `DatabaseConnectionstring` | Postgres. Tables live in schema `lfhub`, migrated at startup. |
| `LFHUB_MASTER_KEY` | 32 bytes, base64. Seals Nightscout tokens, Dexcom accounts and APNs keys (AES-256-GCM). |
| `LFHUB_SETUP_TOKEN` | Root credential for `POST /v1/claim`. |
| `LFHUB_STATUS_KEY` | Optional. Unlocks the details and `fresh` of `/status`; put it in the monitor's URL. |
| `LFHUB_ALLOW_PRIVATE_NIGHTSCOUT` | Optional, `true` to let a Nightscout URL point at a loopback, private or link-local address, and use plain http for a loopback host. For a local test setup. |

Log levels are in `appsettings.json` and can be overridden the usual ASP.NET Core
way (`Logging__LogLevel__Default`).

## Operations

- `/livez` is meant for the orchestrator's liveness probe, `/status` for an
  external monitor; `/healthz` answers as soon as the process serves requests.
- Logs go to the console, one line each; the last 20,000 lines are also kept in
  memory for `GET /v1/device/log`.
- Run one instance per database. The engine runs only in the instance that holds
  a Postgres advisory lock, taken on a connection of its own: another instance
  serves the API, stands by (`standby: true` in the detailed `/status`) and takes
  over within 10 seconds once the lock is free. An instance whose lock connection
  drops (the database restarted) stops its engine at its next check, within 30
  seconds, so two engines can overlap only for that long after such a drop. A
  second replica, or a rolling update that overlaps two instances, does not double
  the pushes. In Kubernetes use `replicas: 1` and the `Recreate` strategy.
- One engine loop serves every patient. A patient's fetches get at most 45
  seconds per evaluation (each request at most 30), so a Nightscout that does
  not answer delays the others' checks by at most that much.
- Every 5 minutes the engine reloads the patients' sources and removes devices
  unseen for 30 days whose push token APNs reports gone (see Security). A
  patient that cannot be followed (no Nightscout URL, and no Dexcom account that
  opens under the master key) is logged as an error at each reload and skipped;
  the engine keeps running.
- Connections to the sources are renewed every 15 minutes, so a changed DNS answer
  is followed.
- On restart the hub fetches everything again; a Nightscout socket that drops
  reconnects on its own, and the patient's data is kept while it does. The
  patient's latest reading (time and value) is also kept in the database,
  written when it changes and at most once a minute, so a hub restarted while
  the sources are down still raises the missed-reading alarm.

## Build, test and deploy

```
dotnet build lfhub.slnx -c Release
dotnet test
```

Warnings are errors, with the recommended .NET analyzers on. The store and API
tests need Postgres: set `LFHUB_TEST_DATABASE` to a connection string whose
user may create databases (each run creates and drops its own); without it they
are skipped. Test fixtures are synthetic.

A push to `main` or `dev` runs the tests (with a Postgres service) and checks formatting;
only then is the image built for linux-x64 and linux-arm64 with
`dotnet publish /t:PublishContainer` and pushed to `ghcr.io/<owner>/<repository>`
as `latest` from `main` and `dev` from `dev`, and as the commit's SHA.

Setting up and running a hub: [docs/SETUP.md](docs/SETUP.md).
