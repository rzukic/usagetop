# usagetop server

A small always-on service that asks every provider where its quota stands, keeps
every answer, and hands the lot to the apps in one call. It exists so the phone
and the PC do not each have to be awake to have a history, and so exactly one
thing holds (and renews) each sign-in.

It reuses the Windows app's own provider and sign-in code
(`../my-usage-win/src/AiUsage.Core`), so a provider fix lands in both.

Three ways to look at it: the page it serves at its own address, the Windows
app and the Android app. Either app switches over when its Settings are given
the server's address and token.

## Run it

```
dotnet run --project src/AiUsage.Server
```

Then, in another shell, give it credentials. The server has to be running:
these commands talk to it rather than to its files.

```
dotnet run --project src/AiUsage.Server -- key mistral       # paste the API key
dotnet run --project src/AiUsage.Server -- key kimi
dotnet run --project src/AiUsage.Server -- signin muse       # device code
dotnet run --project src/AiUsage.Server -- signin claude     # paste the code the page shows
dotnet run --project src/AiUsage.Server -- signin chatgpt    # paste the address the browser lands on
dotnet run --project src/AiUsage.Server -- status
```

| Command | Effect |
|---|---|
| *(none)* or `run` | Run the server |
| `status` | What every provider last reported |
| `refresh` | Ask every provider now (at most once every 30 seconds) |
| `signin <provider>` | Sign in to `claude`, `chatgpt` or `muse` |
| `key <provider>` | Store an API key for `mistral` or `kimi` |
| `signout <provider>` | Forget a provider's credentials |
| `token` | Print the token clients must send |

### The page

Open the address the server listens on (`http://127.0.0.1:8377` by default) in
a browser. It asks once for the token, then shows the same cards and charts as
the apps, and has every sign-in from the commands above as a button. The page
itself is three static files built into the binary; it loads nothing from
anywhere else, and everything it shows comes through the API below.

### More than one account per provider

The server can follow several subscriptions with the same provider: a personal
and a work Claude, two ChatGPT accounts. On the page, **Add an account** under
Sign-ins picks the provider and a name ("Work"), then goes straight on to its
sign-in or key. Each account has its own tokens, card, charts and alerts, and
counts as its own subscription in the combined figure. **Rename** and **Remove
account** are on its tile; the first account of a provider can be signed out but
not removed.

Accounts are keyed `claude`, `claude-2`, `claude-3`, … The first account keeps
the plain key, so everything stored before accounts existed is still its own,
and a number is never reused, so a removed account's history (which stays in the
database) can never be read as a new one's.

A browser sign-in approves whichever account that browser is signed in to, so
sign in to the right one before approving the second account's link.

### Connecting the apps

In either app: **Settings → Server**, enter the address and the token that
`aiusage-server token` prints, and save. The app then asks the server instead of
the providers, takes over the server's history (keeping any older readings of
its own), and leaves its own sign-ins unused. Clearing the address switches it
back.

Accounts added on the server show up in both apps as cards of their own, named
like "Claude · Work". Adding and signing them in happens on the server's page.

The Windows app's **Move this app's sign-ins to the server** button sends what
it holds: API keys are copied, and a Claude or ChatGPT session of the app's own
is sent and then removed from the app. A session the app only borrows from a
CLI cannot be moved; sign in on the server for those.

### Sign in on the server, not by copying

Claude's and ChatGPT's refresh tokens are single-use. If the server were given a
session that an app or a CLI still holds, whichever renewed first would sign the
other out. So `signin` creates a session of the server's own, and the apps should
stop holding one for any provider the server looks after. API keys and Muse's
token are static and safe to use in both places.

The Claude sign-in asks for the `user:profile` scope only, which is all the
usage endpoint needs. The token it stores cannot run models or create API keys.

## Settings

All from the environment.

| Variable | Default | Meaning |
|---|---|---|
| `AIUSAGE_DATA` | `./data` | Database, sealed credentials, the two secrets |
| `AIUSAGE_URLS` | `http://127.0.0.1:8377` | Where to listen; `;`-separated for several |
| `AIUSAGE_SECRET` | *(key file)* | Passphrase the credentials are sealed under |
| `AIUSAGE_API_TOKEN` | *(generated)* | Token clients send as `Authorization: Bearer …` |
| `AIUSAGE_NOTIFY_URL` | *(off)* | ntfy topic URL, or anything that accepts a POSTed line |
| `AIUSAGE_ALERT_BELOW` | `10` | Notify when a window falls under this percent |
| `AIUSAGE_POLL_SECONDS` | `300` | Pace while nothing is moving (minimum 60) |
| `AIUSAGE_ACTIVE_POLL_SECONDS` | `120` | Pace while a window is being spent (minimum 60) |

## Where to put it

It listens on loopback until told otherwise. To reach it from a phone, set
`AIUSAGE_URLS` to the address of a private interface (a Tailscale or WireGuard
address) rather than `0.0.0.0`: the API is plain HTTP, so on an open network the
token would travel in the clear.

A machine at home (a Pi, a NAS, a mini PC) is a better host than a rented one.
ChatGPT's and Meta's edges are known to challenge datacentre addresses, and a
subscription token turning up from a hosting provider is a risk with no upside.

What is on disk:

```
data/usage.db          every reading (SQLite)
data/credentials.json  tokens, AES-256-GCM sealed
data/secret.key        the key they are sealed under, unless AIUSAGE_SECRET is set
data/api-token         the client token, unless AIUSAGE_API_TOKEN is set
```

With `AIUSAGE_SECRET` set, the key is not in the folder and a copy of `data/`
alone opens nothing. Without it the key file sits beside the tokens, which stops
them being read off a backup and nothing more.

### Docker and Coolify

Every release is also an image, for `linux/amd64` and `linux/arm64`:
`ghcr.io/rzukic/usagetop-server`, tagged with the version (`:1.2.1`) and
`:latest`.

```
docker run -d -p 8377:8377 -v usagetop-data:/data \
  -e AIUSAGE_SECRET=… -e AIUSAGE_API_TOKEN=… ghcr.io/rzukic/usagetop-server
```

It listens on port 8377, runs as a non-root user and keeps everything in
`/data`: mount a volume there, or the sign-ins and the history go with the
container. Both variables can be left out: the image then makes a key and a
token of its own in the volume, and `docker exec <container> dotnet
aiusage-server.dll token` prints the token. To update, pull the newer image
and start the container again on the same volume.

To build it yourself: `my-usage-server/Dockerfile` builds from the repository
root, because the server shares the Windows app's core.

```
docker build -f my-usage-server/Dockerfile -t aiusage-server .
docker run -d -p 8377:8377 -v aiusage-data:/data   -e AIUSAGE_SECRET=… -e AIUSAGE_API_TOKEN=… aiusage-server
```

The build stage runs on the building machine's own architecture and publishes
for the image's, so `docker buildx build --platform linux/amd64,linux/arm64`
makes both without compiling under emulation; `release-mac.sh` at the
repository root does that for a release.

On Coolify: a Dockerfile application with base directory `/`, Dockerfile
`/my-usage-server/Dockerfile`, port 8377, a persistent volume on `/data`, and
`AIUSAGE_SECRET` and `AIUSAGE_API_TOKEN` as environment variables. Give it a
domain so it is served over HTTPS; the apps send the API token, and the Windows
app's hand-over sends sign-ins, so plain HTTP across the internet is not enough.
Without a domain of your own, `usage.<ip-with-dashes>.sslip.io` resolves to the
server and gets a certificate like any other name.

### Deploying to Linux

Every release carries the server built for Linux with its runtime included, so
there is no .NET to install: `usagetop-server-linux-x64.tar.gz` for an Intel or
AMD machine, `usagetop-server-linux-arm64.tar.gz` for a Raspberry Pi or an ARM
server.

```
curl -L https://github.com/rzukic/usagetop/releases/latest/download/usagetop-server-linux-x64.tar.gz | tar -xz
./usagetop-server/aiusage-server
```

To update, unpack a newer release over it and restart; the data folder is not
part of the archive. Or build it yourself, as one file:

```
dotnet publish src/AiUsage.Server -c Release -r linux-arm64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

That gives one `aiusage-server` binary of about 95 MB (`linux-x64` for an Intel
box). A unit to keep it running:

```ini
[Unit]
Description=usagetop server
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=/opt/aiusage/aiusage-server
WorkingDirectory=/opt/aiusage
Environment=AIUSAGE_DATA=/var/lib/aiusage
Environment=AIUSAGE_URLS=http://100.64.0.7:8377
EnvironmentFile=-/etc/aiusage.env
User=aiusage
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

Put `AIUSAGE_SECRET=…` in `/etc/aiusage.env`, readable by root only.

Both archives have been started on Ubuntu 24.04 and Debian 12. The one thing
they take from the system is OpenSSL (`libssl`), which any ordinary install
has; a bare container image may not (`apt-get install libssl3`).

## How it polls

Each provider has its own clock.

- **Nothing moving:** every 5 minutes.
- **A window moved in the last 15 minutes:** every 2 minutes.
- **A window is about to refill:** once more, 30 seconds after the refill, so
  the chart shows the edge.
- **Rate limited or unreachable:** 5, then 10, then 15 minutes, back to normal
  on the first good answer. Other providers are not slowed down.
- **Token rejected:** every 15 minutes until someone signs in again.

A reading is stored when the level moved, when its reset time moved, or every
15 minutes while flat. Nothing is ever thinned out: a year is a few megabytes.

## Alerts

With `AIUSAGE_NOTIFY_URL` set, the server posts a line when:

- a window falls under `AIUSAGE_ALERT_BELOW` percent,
- a window that was under it refills,
- a provider stops answering for a reason only you can fix (a rejected token).

Each fires once, on the change. A rate limit or a dropped connection is not
announced. The request is ntfy's: the text as the body, with `Title`,
`Priority` and `Tags` headers.

## API

Every `/api` call needs `Authorization: Bearer <token>`.

| Call | Returns |
|---|---|
| `GET /` , `/app.js`, `/app.css` | The page (no token needed; it holds no data) |
| `GET /healthz` | `ok` (no token needed) |
| `GET /api/state?days=35&points=600` | Snapshot, combined series and every window's series |
| `GET /api/state?since=<epoch ms>` | The same, with only the readings after that moment |
| `GET /api/snapshot` | The latest snapshot alone |
| `POST /api/refresh` | Asks every provider now, returns the snapshot |
| `GET /api/credentials` | Every account: `key`, `provider`, `name`, `displayName`, `removable`, `configured`, `renews`; never the tokens |
| `PUT /api/credentials/{account}` | `{ "token": … }`, or a whole session with `refreshToken`, `expiresAt`, `accountId`; also `kimiChina`, `mistralBudget`, `mistralSpent` |
| `DELETE /api/credentials/{account}` | Signs the account out |
| `POST /api/accounts` | `{ "provider": "claude", "name": "Work" }` adds an account; returns it, with its key |
| `PATCH /api/accounts/{account}` | `{ "name": … }` renames it; `null` clears the name |
| `DELETE /api/accounts/{account}` | Removes an added account and its tokens (not the first of a provider) |
| `POST /api/signin/{account}/start` | `{ kind, url, userCode, instructions }` |
| `POST /api/signin/{account}/complete` | `{ "input": "<what the user pasted>" }` |
| `GET /api/signin/{account}/status` | `none`, `pending`, `done` or `failed: …` |

`{account}` is an account key: `claude` for the first, `claude-2` and on for ones
added since. The CLI commands take the same keys (`aiusage-server signin claude-2`).

`/api/state` is shaped like what the Android app already stores, so a client can
put the response where its own fetch would have:

```json
{
  "serverTime": 1794428250000,
  "snapshot": {
    "fetchedAt": "2026-11-11T20:17:30.000Z",
    "combinedPercent": 46.2,
    "combinedProviders": 2,
    "usages": [
      {
        "id": "mistral", "account": "mistral", "accountName": null,
        "state": "OK", "plan": "Education", "message": null,
        "fetchedAt": "2026-11-11T20:17:30.000Z", "stale": false, "transient": false,
        "windows": [
          { "label": "Vibe", "remainingPercent": 77.25, "resetAt": "2026-12-01T00:00:00.000Z",
            "detail": "€98.49 of €127.50 left", "countsTowardCombined": true }
        ]
      }
    ]
  },
  "combined": [[1794428250000, 46.2]],
  "series": { "mistral|Vibe": [[1794428250000, 77.25]] }
}
```

`state` is `OK`, `NOT_CONFIGURED` or `ERROR`. `id` is the provider and `account`
the account; series are keyed by account (`claude-2|7d`). A series longer than `points` is
cut down by keeping each time slice's lowest and highest reading, so refills
stay vertical.

## Layout

```
src/AiUsage.Server/
  Program.cs        wiring, start-up log
  ServerOptions.cs  environment settings, the two secrets
  Poller.cs         the loop; the only thing that talks to a provider
  Schedule.cs       when each provider is asked next
  Store.cs          SQLite: readings, combined level, last snapshot
  Alerts.cs         what is worth a notification, and sending it
  SignIn.cs         sign-in for a machine with no browser
  Api.cs            the HTTP endpoints
  Wire.cs           the JSON the apps are sent, chart downsampling
  WebUi.cs          serves the page
  Web/              the page: index.html, app.css, app.js
  Cli.cs            the commands above
tests/AiUsage.Server.Tests/
```

```
dotnet test tests/AiUsage.Server.Tests
```

## Not done yet

- **Handing a session over from the Android app.** Only the Windows app has
  the button. On a phone, sign in on the server's page instead.
- **Untested against the real services:** the Claude and ChatGPT sign-ins on
  the server (they need a person at a browser), moving a real Claude or ChatGPT
  session across from the Windows app, and delivery to a real notify URL.
  Polling, the API, the app client and moving a key are verified against a
  running server.
- **The page has not been looked at in a browser.** Its script is exercised
  against a stand-in for one, which catches errors but not layout.
- **HTTPS.** Keep it on a private network, or put a TLS proxy in front. Tokens,
  and a session being moved across, travel in the clear otherwise.
