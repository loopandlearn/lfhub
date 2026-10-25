# Setting up LF Hub

LF Hub is a small server that follows one person's glucose data (Nightscout,
Dexcom Share or both) and drives LoopFollow on the family's iPhones: the badge,
the Live Activity, the alarms and Speak BG, while LoopFollow itself is closed.
One hub serves one family and one person.

> LF Hub is alpha software. It is not a medical device and not medical advice.
> Alarms depend on the hub, the network and Apple's push service, and any of them
> can fail. Keep LoopFollow's own alarms in mind as the fallback, and talk to your
> care team about how you rely on alerts.

## What you need

- A machine that is always on and can run containers: a small cloud server, a
  home server or a Raspberry Pi (the image is built for arm64 and amd64).
- PostgreSQL 14 or later. It can run next to the hub.
- A domain name and HTTPS in front of the hub (a reverse proxy such as Caddy,
  Traefik or nginx). The hub speaks plain HTTP on port 8080 and must only be
  reached through HTTPS.
- LoopFollow on iOS 26 or later on each phone, with an APN key under
  Settings → APN on the phone that sets up the hub. Family members on the same
  LoopFollow build (the same Apple team) need no key of their own.

## 1. Create the secrets

Create three random values and keep them in a password manager. The hub cannot
recover them.

```sh
openssl rand -base64 32   # LFHUB_MASTER_KEY: encrypts the stored passwords and keys
openssl rand -base64 24   # LFHUB_SETUP_TOKEN: makes a phone an owner (admin) of the hub
openssl rand -base64 24   # LFHUB_STATUS_KEY: optional, for a monitoring service
```

- **LFHUB_MASTER_KEY** must be exactly 32 bytes, base64-encoded, as the command
  makes it. If it is lost or changed, the stored Nightscout token, Dexcom login
  and APN keys can no longer be read: the phones send their APN keys again on
  their own, and an owner must send the data source again.
- **LFHUB_SETUP_TOKEN** is the hub's master password. Use it only to set up your
  own phones. Family members join with an invite from the app instead.

## 2. Run it

### Docker Compose

```yaml
services:
  db:
    image: postgres:16
    restart: unless-stopped
    environment:
      POSTGRES_USER: lfhub
      POSTGRES_PASSWORD: change-me
      POSTGRES_DB: lfhub
    volumes:
      - db:/var/lib/postgresql/data

  lfhub:
    image: ghcr.io/loopandlearn/lfhub:latest
    restart: unless-stopped
    depends_on: [db]
    environment:
      DatabaseConnectionstring: Host=db;Port=5432;Username=lfhub;Password=change-me;Database=lfhub
      LFHUB_MASTER_KEY: ${LFHUB_MASTER_KEY}
      LFHUB_SETUP_TOKEN: ${LFHUB_SETUP_TOKEN}
      LFHUB_STATUS_KEY: ${LFHUB_STATUS_KEY}
    ports:
      - "127.0.0.1:8080:8080"

volumes:
  db:
```

Put the three secrets in a `.env` file next to `docker-compose.yml` (and keep that
file private), then run `docker compose up -d`. Point your reverse proxy's HTTPS
site at `127.0.0.1:8080`. With Caddy, the whole site is:

```
hub.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

### Kubernetes

Run exactly one hub per database. A second instance only stands by (the hub
holds a database lock), so use one replica and the `Recreate` strategy so an
upgrade never runs two side by side.

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: lfhub
type: Opaque
stringData:
  DatabaseConnectionstring: Host=postgres;Port=5432;Username=lfhub;Password=change-me;Database=lfhub
  LFHUB_MASTER_KEY: <from step 1>
  LFHUB_SETUP_TOKEN: <from step 1>
  LFHUB_STATUS_KEY: <from step 1>
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: lfhub
spec:
  replicas: 1
  strategy:
    type: Recreate
  selector:
    matchLabels: { app: lfhub }
  template:
    metadata:
      labels: { app: lfhub }
    spec:
      containers:
        - name: lfhub
          image: ghcr.io/loopandlearn/lfhub:latest
          ports:
            - containerPort: 8080
          envFrom:
            - secretRef: { name: lfhub }
          readinessProbe:
            httpGet: { path: /healthz, port: 8080 }
          livenessProbe:
            httpGet: { path: /livez, port: 8080 }
            periodSeconds: 30
            failureThreshold: 4
---
apiVersion: v1
kind: Service
metadata:
  name: lfhub
spec:
  selector: { app: lfhub }
  ports:
    - port: 8080
```

Expose the service through your ingress with TLS.

Check that it answers: `https://hub.example.com/healthz` should say `ok`.

## 3. Connect the first phone

1. In LoopFollow, make sure Nightscout and/or Dexcom work and that an APN key is
   set under Settings → APN.
2. Open Settings → LoopFollow Hub → Set Up as Owner. Enter the hub's address
   (`https://hub.example.com`), the setup token and the name of the person you
   follow.
3. The phone sends its data source, alarms and settings to the hub. Set
   Settings → Background Refresh to LoopFollow Hub.
4. Work through the Ready list on the LoopFollow Hub page until every row is
   green, then send a test alarm with the phone locked.

## 4. Add the family

On an owner's phone, open LoopFollow Hub → Invite Family Member. It shows a QR
code that works once, within 24 hours. On the new phone, either choose
"Join a Family Hub" when LoopFollow starts for the first time, or open
Settings → LoopFollow Hub → Join with Invite, and scan the code.

The new phone can copy the alarms of an existing phone, start fresh, or take
the place of an old phone of a member. It receives the data source from the hub,
so it can still alarm by itself if the hub stops answering.

An owner can make another phone an owner, change a phone back to a member, and
remove phones. A member's data source and APN settings are managed by the hub.

## 5. Watch it

Add a monitor (for example UptimeRobot) for

```
https://hub.example.com/status?key=<LFHUB_STATUS_KEY>&fresh=1
```

It answers 200 while the hub, its database and the data source are healthy, and
503 otherwise, including when the latest reading is 12 minutes old or more.
`/status` without the key tells only whether the hub is up.

## Backups

Back up the database regularly, for example with
`pg_dump -Fc -U lfhub lfhub > lfhub.dump`, and keep the master key in a
separate safe place: the stored passwords and keys in a backup can only be read
with it.

## Upgrades

Pull the new image and restart the hub. It updates the database on start, by
adding tables and columns, so the version before can run on it again if an upgrade
has to be rolled back. A hub older than the phones' LoopFollow, or the other way
around, shows on the LoopFollow Hub page, and the phone keeps alarming by itself
until both understand each other.
