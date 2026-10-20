# Contributing to Spindle

Thanks for wanting to help. Bug reports, ideas and pull requests are all welcome. Everyone taking part follows the
[code of conduct](CODE_OF_CONDUCT.md).

Before proposing a feature, read [docs/principles.md](docs/principles.md): it says what Spindle is for and what it deliberately
doesn't do (no tracking of apps or windows, no guessing a whole day from logs, suggestions you confirm, never automatic changes). For a
larger change, open an issue first, so we can agree on the approach before you spend time on it.

## The repository

| Path | Contents |
|---|---|
| `src/Spindle.Domain` | Entities and business rules. No dependencies. |
| `src/Spindle.Infrastructure` | EF Core on PostgreSQL, ASP.NET Core Identity, migrations. |
| `src/Spindle.Api` | Minimal API: authentication and endpoints. In production it also serves the web app. |
| `src/web` | Web app: React, TypeScript, Vite, TanStack Router and Query, Tailwind, shadcn/ui. |
| `android` | The Android app in Kotlin and Jetpack Compose. See `android/README.md`. |
| `apple` | The Mac and iPhone apps in Swift and SwiftUI, and the Swift package they share. See `apple/README.md`. |
| `windows` | The Windows app in C# and Avalonia, and the library that reads agent logs there. See `windows/README.md`. |
| `branding` | The logo, app icons, brand colors and font, and the scripts that draw them and the store pictures. |
| `tests/Spindle.Tests` | Unit tests, in-memory API tests, and integration tests against PostgreSQL. |
| `infra/docker-compose.yml` | Local PostgreSQL and Mailpit for development and tests. |
| `scripts/local-db.sh` | Sets up the local database in one step. |
| `scripts/dev.sh` | Runs the database, the API and the web app together. |
| `scripts/check.sh` | Runs the pull-request checks of GitHub Actions on this Mac, for what the branch changed (or `all`). |
| `Dockerfile` | Linux image containing the API and web app. See `docs/self-hosting.md`. |
| `deploy/` | Docker Compose and settings for running your own server, not for development. See `docs/self-hosting.md`. |
| `openapi/spindle.json` | Regenerated on every build and committed. The web, Apple and Android clients are generated from it. |

Rules the code follows (IDs, sync, authentication, reports, rates and more) are in
[docs/architecture.md](docs/architecture.md). Split entries and reading agent logs are in
[docs/splits-and-suggestions.md](docs/splits-and-suggestions.md). [docs/](docs/README.md) lists the rest.

## Running it

### Setup

Requires the .NET SDK pinned in `global.json`, Node.js 22+ and Docker. On macOS, [Colima](https://github.com/abiosoft/colima) is enough: `brew install colima docker docker-compose`.

```sh
./scripts/local-db.sh
```

The script starts PostgreSQL 18 and Mailpit (which catches every email the app sends; read them at http://localhost:8025) in Docker (`infra/docker-compose.yml`, localhost only, data kept in a volume) and generates a PostgreSQL password into the git-ignored `infra/.env`. It then stores the connection strings and a token signing key in user secrets and applies the migrations. It is safe to run again, e.g. after a reboot.

To use another PostgreSQL instead, set the secrets yourself. The database user must own the database to apply migrations. Integration tests also need permission to create databases. Secrets go in user secrets, never in `appsettings*.json`:

```sh
dotnet user-secrets set "ConnectionStrings:Default" "Host=HOST;Port=5432;Database=DB;Username=USER;Password=PASSWORD" --project src/Spindle.Api
dotnet user-secrets set "Auth:SigningKey" "$(openssl rand -base64 48)" --project src/Spindle.Api
dotnet tool restore
dotnet ef database update --project src/Spindle.Infrastructure --startup-project src/Spindle.Api
```

Run everything and open http://localhost:5173:

```sh
./scripts/dev.sh
```

Or separately: `dotnet run --project src/Spindle.Api` for the API on port 5258, and `npm run dev` in `src/web` for the web app on port 5173. The Vite dev server proxies `/api` to the API, so the browser sees one origin, as in production.

In Development the OpenAPI document is served at `http://localhost:5258/openapi/v1.json`. `/health` reports whether the database is reachable. `src/Spindle.Api/Spindle.Api.http` walks through register, login, `/api/me`, refresh and logout.

### Tests

```sh
dotnet test                 # backend: unit, in-memory API and PostgreSQL integration tests
cd src/web
npm run typecheck && npm run lint
npm test                    # web unit tests (Vitest)
npm run test:e2e            # real browsers (Chromium, WebKit) against the real API; starts both servers
```

Before the first end-to-end run, install the browsers with `npx playwright install chromium webkit`. The other apps' tests are described in their READMEs (`apple/`, `windows/`, `android/`), and `scripts/check.sh all` runs
them all.

Integration tests (`tests/Spindle.Tests/Integration`) run the API against a real PostgreSQL. Each run creates a database named `Spindle_Tests_<guid>` by applying the migrations, and drops it at the end. They use the server from the `ConnectionStrings:PostgresTests` user secret (id `spindle-tests`, set by `scripts/local-db.sh`), or from the `ConnectionStrings__PostgresTests` environment variable in CI.

### Changing the API

After changing endpoints or contracts, build the API (which regenerates `openapi/spindle.json`), then regenerate the web client types with `npm run generate:api` in `src/web`, the Swift client with `apple/scripts/generate-api.sh`, and Android models with `python3 android/scripts/generate-models.py`.

### Changing the schema

```sh
dotnet ef migrations add <Name> --project src/Spindle.Infrastructure --startup-project src/Spindle.Api --output-dir Persistence/Migrations
```

## Pull requests

- Keep a pull request to one change, with tests for what it changes.
- Run `scripts/check.sh` before opening it. It runs what CI runs, for the parts of the repository your branch changed
  (or everything with `scripts/check.sh all`). The Apple checks need a Mac with Xcode.
- Commit messages follow the repository's style: `feat(scope): what it does`, `fix(scope): …`, `docs: …`, in the
  present tense and in plain words, e.g. `fix(mac): quick entry panel opens at its content's height`.
- Code, comments, commits and docs are in English. Match the code around yours: its naming, its comments and how much
  it says.
- If you work with an AI agent, [AGENTS.md](AGENTS.md) tells it where to start.

## The contributor license agreement

The first time you open a pull request, a bot asks you to agree to the [contributor license agreement](CLA.md) by
commenting on it with the sentence it gives you. You keep the copyright to what you write; the agreement gives
Threennovate d.o.o., which runs Spindle, the right to use and license your contribution.

We ask for it for two reasons. The iPhone app is distributed through the App Store, whose terms don't sit well with the
AGPL alone, so we need to be able to ship your code there under other terms. And it leaves room to change the license
later if we have to. Whatever happens, the code you contributed stays available under the AGPL.

## Security

Please don't report vulnerabilities in public issues. See [SECURITY.md](SECURITY.md).
