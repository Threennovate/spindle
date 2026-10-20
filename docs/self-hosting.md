# Running your own server

How to run the Spindle server yourself. It is the same app as Spindle Cloud, with nothing locked away: no trial, no
plan and no limits.

The server is one Linux image with the API and the web app, and a PostgreSQL 18 database next to it. The image listens
on port **8080**, runs as UID 1654, and has no Node or .NET SDK in it. Released images are at
`ghcr.io/threennovate/spindle`, and the root `Dockerfile` builds the same image from the code.

## Quick start

You need a machine with Docker and Docker Compose. [deploy/](../deploy) has everything else: a `docker-compose.yml`
with the app and PostgreSQL, and a `.env.example` with the settings.

```bash
git clone https://github.com/Threennovate/spindle.git
cd spindle/deploy
cp .env.example .env
```

Fill in `.env`. The four settings at the top are required:

- `App__BaseUrl`: the address people open, e.g. `https://spindle.example.com`. To try it on your own computer first,
  use `http://localhost:8080`.
- `AllowedHosts`: that address's host name, e.g. `spindle.example.com` (or `localhost`).
- `POSTGRES_PASSWORD`: a random password for the database, e.g. from `openssl rand -hex 32`.
- `Auth__SigningKey`: a random secret of at least 32 characters, e.g. from `openssl rand -base64 48`.

Then start it:

```bash
docker compose up -d
```

The first start creates the database and applies the migrations, which takes a few seconds. `docker compose ps` shows
the app as `healthy` when it's ready, and `curl http://localhost:8080/health` answers `Healthy`. Open the address,
create your account, and turn off registration if strangers can reach the server (see below).

Without the rest of `.env`, the app has no email: everything works, but nobody gets an email to confirm their address,
reset a password or accept an invitation. To see those emails while you try Spindle, start the bundled mail catcher
with `docker compose --profile mailpit up -d`, set the three `Smtp__` lines for Mailpit in `.env`, and read the mail at
`http://localhost:8025`.

### Building the image yourself

To run code you changed, build the image from the checkout:

```bash
docker compose up -d --build
```

It builds the root `Dockerfile` (a few minutes the first time) and tags the result with the image name in the compose
file, so later `docker compose up -d` uses your build. `docker compose pull` replaces it with the released image again.
When the released image can't be pulled, `docker compose up -d` builds it from the checkout as well.

## Settings

The app reads its settings from environment variables. With the compose file they come from `.env`; every line there
reaches the app. `ASPNETCORE_ENVIRONMENT`, the connection string, `Database__MigrateOnStartup` and
`App__DownloadsPath` are set in `docker-compose.yml`, so there is nothing to choose.

- `App__BaseUrl`: the full public address. Email links point there, and passkeys in the browser belong to its domain,
  so set it before people create passkeys.
- `AllowedHosts`: the host names the app answers to, separated by `;`. The compose file adds `localhost` for its health
  check.
- `Auth__SigningKey`: signs sign-in tokens. Changing it signs everyone out.
- `Auth__AllowRegistration=false`: only people with an invitation can create an account. Create your own account first,
  then set it and run `docker compose up -d` again.
- `Operators__Emails__0`, `__1`, …: the people who run the server. Once they have confirmed their email, they see the
  Operator page (in the account menu): every organization with its owner and seats, but nobody's tracked time.
- `Smtp__*`: the SMTP server for email, see [Email](#email) below.
- `App__TermsUrl`, `App__PrivacyUrl` and `App__TermsVersion`: terms of service and a privacy policy that new accounts
  must agree to, e.g. for a server open to people outside your team. Leave them out and nobody is asked to agree to
  anything. `App__TermsVersion` is kept with each account that agrees; change it when the terms change.
- `App__SourceUrl`: where people get the source code of the version you run, linked as **Source code** in the web
  app. It defaults to Spindle's repository; if you run a modified version, point it at yours, as the AGPL asks.
- `SPINDLE_IMAGE`, `SPINDLE_PORT` and `SPINDLE_SUBNET`: which image runs, where it listens on the machine, and the
  Docker network's address range. `.env.example` explains them.

Trials and paid plans (`Subscriptions__Enabled`) belong to Spindle Cloud. The compose file keeps them off, so nobody on
your server is ever limited and the apps show no trial or plan.

### Without the compose file

Any Docker host works, and so does a platform such as Coolify or Kubernetes, with the app and PostgreSQL on the same
private network. Set the same variables at runtime (never as build arguments or in git), plus the ones the compose file
sets for you:

- `ConnectionStrings__Default`: `Host=HOST;Port=5432;Database=spindle;Username=spindle;Password=SECRET`. This is
  Npgsql key/value syntax, not a `postgres://` URL. Quote values that contain `;` or quotes.
- `ASPNETCORE_ENVIRONMENT=Production`, `Database__MigrateOnStartup=true`.
- `Smtp__Host` and `Smtp__FromAddress`: the app doesn't start without them.
- `App__DownloadsPath=/app/downloads`, if the server should offer app downloads (see [Downloads](#downloads)).

[deploy/server.env.example](../deploy/server.env.example) lists them in one place.

Use `postgres:18-alpine` (or another PostgreSQL 18) and don't expose it publicly. PostgreSQL 18 keeps its data in
`/var/lib/postgresql`, not `/var/lib/postgresql/data` as older versions did, so mount the volume there. Give the app
its own role and a database owned by that role, e.g. both called `spindle`. The app applies its schema migrations
itself, so it needs to own its database, but not superuser rights or access to other databases. The first migration
installs the trusted `citext` extension. Default PostgreSQL memory settings are fine for a small team; measure before
tuning.

Run one instance of the app: live sync and the hourly background job assume one.

## Email

Spindle sends a few emails while someone waits for them: confirming an email address, resetting a password and
invitations to a workspace. It sends them through any SMTP server:

- `Smtp__Host` and `Smtp__Port`.
- `Smtp__Security`: `StartTls` on port 587, `SslOnConnect` on port 465, or `None` only for a local mail catcher.
- `Smtp__UserName` and `Smtp__Password`, if the server needs them.
- `Smtp__FromAddress`, and `Smtp__FromName` if it shouldn't be "Spindle".
- `Smtp__ReplyToAddress`, when nobody reads the sender address (e.g. a no-reply address).

The app gives up on an unreachable server after half a minute, and a failed email doesn't stop what the person was
doing: the account is still created, and the error is in the app's log (`docker compose logs app`).

For a server other people use, send through a transactional email service rather than a personal mailbox or your
company's own mail server: its limits fit an app, it tells you about bounces, and complaints about one email don't
hurt the mail your team sends. Send from a domain you control, with SPF, DKIM and DMARC records, so the emails don't
land in spam. Any service with SMTP works, e.g. Resend, Postmark, Amazon SES, or Brevo and Scaleway to stay in the EU.

### Example: Resend

1. In Resend, add a domain under **Domains**. Use a subdomain only for this, e.g. `mail.example.com`, so its
   reputation is separate from your other mail, and pick the region closest to your people (it can't be changed later
   without adding the domain again).
2. Add the records Resend shows to your DNS, exactly as shown. With Cloudflare, turn proxying off for them. Wait until
   the domain is verified, usually a few minutes.
3. If the domain has no DMARC record yet, add a TXT record `_dmarc.mail.example.com` with `v=DMARC1; p=none;` to start
   with, and make it stricter once mail arrives as it should.
4. Under **API Keys**, create a key with **Sending access** limited to that domain.
5. Set:

   ```
   Smtp__Host=smtp.resend.com
   Smtp__Port=465
   Smtp__Security=SslOnConnect
   Smtp__UserName=resend
   Smtp__Password=re_YOUR_API_KEY
   Smtp__FromAddress=noreply@mail.example.com
   Smtp__ReplyToAddress=hello@example.com
   ```

   Port 587 with `StartTls` works too, and 2465 or 2587 if your host blocks the usual ports.
6. Run `docker compose up -d` again, and reset your own password or invite yourself to check. Resend's **Emails** page
   shows each email and whether it was delivered.

Resend's free plan has a daily limit (100 emails at the time of writing), which is enough for a small team. Many
invitations at once can run into it.

## Sign in with Google and Apple

People can sign in and create an account with "Continue with Google" and "Continue with Apple", on the web and in the
apps, once the server has credentials for them. Without them the buttons don't show. They are your own: a Google Cloud
OAuth client, and for Apple a Services ID and a Sign in with Apple key from an Apple Developer account. Apple needs
HTTPS; Google allows `http://localhost` for trying it out.

`scripts/setup-sign-in.sh` walks you through both consoles step by step and saves the settings to `deploy/.env`:

```bash
scripts/setup-sign-in.sh
```

The settings it produces:

- `Auth__Google__ClientId` and `Auth__Google__ClientSecret`: a client of type **Web application** whose authorized
  redirect URI is `App__BaseUrl` plus `/api/auth/external/google/callback`.
- `Auth__Apple__ServicesId`, `Auth__Apple__TeamId`, `Auth__Apple__KeyId` and `Auth__Apple__PrivateKey`: the Services
  ID, with your domain and the return URL `App__BaseUrl` plus `/api/auth/external/apple/callback`; your team; and the
  key, with the text of its `.p8` file on one line, line breaks written as `\n`.

An account is matched by the email address Google or Apple confirm, so someone who already has an account signs in to
it, as long as the provider runs that mailbox (Gmail, a Google Workspace domain, iCloud or Apple's relay). Any other
address only makes a new account, which confirms the address by email. People who choose **Hide My Email** with Apple get an address at `privaterelay.appleid.com`; register your
`Smtp__FromAddress` (or its domain) under **Sign in with Apple for Email Communication** in the Apple Developer account,
or they get no email. With `Auth__AllowRegistration=false`, Google and Apple sign in only people who have an account or
an invitation.

## HTTPS and a reverse proxy

Put the app behind a reverse proxy that terminates HTTPS and forwards WebSockets, which live sync (SignalR) needs.
Browsers allow passkeys and the app's sign-in cookie only over HTTPS (or on `localhost`), and the apps expect it.

The compose file publishes the app only on this machine, at `127.0.0.1:8080`, so a proxy on the same machine can reach
it and nothing else can. With [Caddy](https://caddyserver.com), which gets and renews certificates by itself, the whole
`Caddyfile` is:

```
spindle.example.com {
	reverse_proxy 127.0.0.1:8080
}
```

nginx, Traefik or a platform's own proxy work the same way; for nginx, pass the `Upgrade` and `Connection` headers so
WebSockets work, and set `X-Forwarded-For` and `X-Forwarded-Proto`.

Agents connect to the MCP server at `App__BaseUrl` plus `/mcp`, and sign in through `/oauth/…` and `/.well-known/…` on
the same address (see [connect-agents.md](connect-agents.md)). Pass those paths through as they are, and don't buffer
responses on `/mcp`, which may stream.

The app takes the client's address and scheme from the proxy's `X-Forwarded-*` headers only when the proxy is on a
network it trusts. The compose file trusts its own Docker network (`SPINDLE_SUBNET`), which is where requests from a
proxy on the same machine or in the same network come from. Without that, every request would seem to come from the
proxy, and the limits on emails per address would apply to everyone at once. If your proxy is elsewhere, list its
network with `ReverseProxy__KnownNetworks__0` (and `__1`, …) in `.env`, e.g. the subnet of a Docker network it shares
with the app (`docker network inspect NETWORK --format '{{range .IPAM.Config}}{{.Subnet}}{{println}}{{end}}'`). List
only networks that contain the proxy, and never `0.0.0.0/0`: the app trusts one proxy hop.

`/health` reports whether the app and its database work. The compose file checks it every 30 seconds, with a minute's
grace at startup for the migrations; a platform's health check should do the same over HTTP on port 8080.

## Upgrading

Each release is published as `ghcr.io/threennovate/spindle` tagged with its version (`1.2.0`), its minor and major
versions (`1.2`, `1`) and `latest`. To upgrade:

```bash
docker compose pull
docker compose up -d
```

The new app applies new database migrations as it starts (`Database__MigrateOnStartup=true`), and is healthy once they
are done. Back up the database first (below): migrations only move forward, so going back to an older image means
restoring the backup taken before the upgrade.

To stay on a version until you choose to move, set `SPINDLE_IMAGE=ghcr.io/threennovate/spindle:1.2` (new patches
only) or `:1.2.0` (exactly that) in `.env`. If you build the image yourself, `git pull` and
`docker compose up -d --build` instead.

## Backups and restoring

Everything Spindle keeps is in the PostgreSQL database, in the `postgres-data` volume. Back it up daily to somewhere
outside the server, e.g. with cron:

```bash
docker compose exec -T postgres pg_dump -U spindle -d spindle --format=custom > spindle-$(date +%F).dump
```

Run it from the `deploy` folder. The dump is consistent while the app runs. Keep several days of dumps, and try
restoring one now and then, so you know it works before you need it.

To restore a dump, stop the app, replace the database, and start the app again:

```bash
docker compose stop app
docker compose exec -T postgres dropdb -U spindle --force spindle
docker compose exec -T postgres createdb -U spindle spindle
docker compose exec -T postgres pg_restore -U spindle -d spindle --no-owner < spindle-2026-10-11.dump
docker compose start app
```

On a new machine, start only the database first (`docker compose up -d postgres`) with the same `.env`, restore, and
then `docker compose up -d`. Keep a copy of `.env` with the backups.

A database dump doesn't contain the downloads folder (the `downloads` volume), so back that up separately if you use
it.

## Downloads

The app serves the folder in `App__DownloadsPath` at `/downloads`, e.g. a Mac app's disk image and its update feed. In
the compose file it is the `downloads` volume at `/app/downloads`. It starts empty, and the files must be readable by
UID 1654. The web app shows **Download for Mac** only when `/downloads/Spindle.dmg` is there, and the Windows
downloads only when their installers are ([releasing.md](releasing.md#windows)).

You need it only if you build and sign the apps yourself (below). The released apps update from Spindle Cloud's
downloads, whichever server they sign in to.

## What works differently on your own server

The Mac, Windows and Android apps work with your server: they offer "Your own server" when you sign in, and you type
the address you open in a browser. A few things depend on Spindle Cloud's domain or on our credentials, and don't work
against another server:

- **The iPhone app** from the App Store connects only to Spindle Cloud for now. It can't offer another server yet; to
  use it with yours, build it with your server's address (below). The web app works on a phone in the meantime.

- **Passkeys in the apps.** A passkey belongs to one domain, and an app may use it only if the domain and the signed
  app name each other. The released apps are signed for Spindle Cloud's domain, so on your own server they sign in
  with email and password. Passkeys in the browser work on your server, over HTTPS.
- **Push notifications on iPhone.** Apple delivers them only from a server that holds the key of the team that signed
  the app, and that key is ours. If you build the iPhone app yourself, it is signed by your team and push can use your
  key: set `Push__Apns__KeyId`, `Push__Apns__TeamId`, `Push__Apns__PrivateKey` and `Push__Apns__BundleId` (see
  `deploy/server.env.example`).
- **Hosted-only features**, now or later: calendar and issue tracker integrations, and e-invoicing. They depend on our infrastructure, on credentials with other services, or cost
  us per user. Their code is open too, and where it makes sense you can turn them on with your own credentials.
  Everything for tracking and dividing time is in every server: reading agent logs on the Mac and Windows, splits and suggestions,
  the MCP for agents, reports, exports, teams and the API.

## Building the apps for your own server

If you build and sign the apps yourself, e.g. to use the iPhone app, passkeys or push with your own server, change
these. This only
says where they are; each platform's own docs explain how to build and release.

**Apple** (Mac and iPhone; see [apple/README.md](../apple/README.md)):

- [apple/project.yml](../apple/project.yml): `DEVELOPMENT_TEAM` (your team ID), `bundleIdPrefix` and each target's
  `PRODUCT_BUNDLE_IDENTIFIER` (`com.spindletrack.mac`, `.iphone`, `.iphone.widgets`), and `SPINDLE_SERVER_URL`
  in the Release configurations: the server the app starts with, also where the Mac app looks for updates. Run
  `xcodegen` in `apple/` afterwards.
- `apple/Mac/Resources/Spindle.entitlements` and `apple/iOS/Resources/Spindle.entitlements`: the
  `webcredentials:` domain, for passkeys.
- [src/Spindle.Api/Web/AppleAssociation.cs](../src/Spindle.Api/Web/AppleAssociation.cs) on the server: your team ID
  and bundle IDs, so your domain names your apps back.
- Mac updates are signed with a Sparkle key (`SPINDLE_UPDATES_PUBLIC_KEY` in `project.yml`) and served from your
  server's downloads folder; see [releasing.md](releasing.md#mac).

**Android** ([releasing.md](releasing.md#android)): `namespace`, `applicationId` and the `SERVER_URL` build field
in [android/app/build.gradle.kts](../android/app/build.gradle.kts), and the `targetPackage` of the shortcuts in
`android/app/src/main/res/xml/shortcuts.xml`.

**Windows** ([releasing.md](releasing.md#windows)): `SpindleUpdatesUrl` in
[windows/Directory.Build.props](../windows/Directory.Build.props), the default `ServerUrl` in
`windows/Spindle.Platform/PreferencesStore.cs`, `CloudHost` in `windows/Spindle.Windows/Views/AuthView.cs`, and the
installer's `--packId` in `windows/scripts/release-windows.sh`.

The names Spindle and its logo are not under the AGPL; if you hand your builds to other people, read
[TRADEMARKS.md](../TRADEMARKS.md) first.
