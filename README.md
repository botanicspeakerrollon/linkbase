# Linkbase

Load any connected data, such as people and projects, suppliers, a rail network or a climbing logbook, then explore it as a
graph and edit it, with no query language and no graph database. One ASP.NET Core app (Blazor Interactive Server) and
Postgres, with a database per model.

It started as a personal climbing logbook, which is still its one model with code of its own. See
[models](docs/models.md) for the rest.

## Prerequisites

- [.NET 10 SDK](https://dot.net) (the exact version is pinned in `global.json`)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) for the database and for the database tests

## First-time setup

```powershell
dotnet tool restore
./scripts/init-dev.ps1
```

`init-dev.ps1` creates a random database password in `.env` (git-ignored) and stores the matching
connection string in `src/Linkbase/appsettings.Development.local.json` (also git-ignored). The Anthropic key still goes in
[user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets).

To read text with AI later, add your Anthropic API key too:

```powershell
dotnet user-secrets set Anthropic:ApiKey "<your key>" --project src/Linkbase
```

## Everyday commands

| Task | Command |
| --- | --- |
| Start the database | `docker compose up -d --wait` |
| Apply migrations | `dotnet ef database update --project src/Linkbase` |
| Run the app | `dotnet run --project src/Linkbase` (or `dotnet watch --project src/Linkbase`) |
| Run all tests (needs Docker and Chromium) | `dotnet test -c Release` |
| Run tests that don't need a browser | `dotnet test -c Release --filter "Category!=Browser"` |
| Run tests that don't need Docker | `dotnet test -c Release --filter "Category!=Database&Category!=Browser"` |
| Measure coverage | `dotnet test -c Release --settings coverage.runsettings --collect "Code Coverage;Format=Cobertura"` |
| Add a migration | `dotnet ef migrations add <Name> --project src/Linkbase --output-dir Data/Migrations` |
| Stop the database | `docker compose down` |
| Load a model file | `dotnet run --project src/Linkbase --launch-profile http -- model load <file> <model>` (see [models](docs/models.md)) |
| Back up the database | `./scripts/backup-db.ps1` (see [operations](docs/operations.md) for restoring and rebuilding) |
| Look at the tables and query them | `docker exec -it -e PGOPTIONS="-c default_transaction_read_only=on" linkbase-db-1 psql -U linkbase -d linkbase` (read-only; see [Postgres](docs/postgres.md)) |

The app runs at <http://localhost:5173>. Its home page shows whether the database is reachable and
up to date. The user guide is in the app, under Guide in the rail (`/guide`), with a page of its own for each domain
(`/guide/climbing`). A domain adds its page through `IDomainModule.GuideHref`. `docker compose down` keeps your data; adding `-v` deletes it.

### Tests

There are three kinds. Unit tests need nothing. Database tests, including those that render pages with bUnit, start
their own Postgres in Docker, so they never touch your data. Browser tests start the real app against that
same test database and drive it in Chromium, which has to be installed once, after a build:

```
powershell -ExecutionPolicy Bypass -File tests/Linkbase.Tests/bin/Release/net10.0/playwright.ps1 install chromium
```

Without Docker, point the tests at any Postgres server whose user can create databases. Each run creates its own databases
there and drops them all at the end:

```powershell
$env:LINKBASE_TEST_CONNECTION = "Host=...;Database=postgres;Username=...;Password=..."
dotnet test -c Release
```

**CI** runs the same build and every test on GitHub Actions for each push to `main` and each pull request
(`.github/workflows/ci.yml`), on Ubuntu with the runner's Docker. Results are kept with each run for two weeks. The
performance test is skipped there, as it is locally unless `LINKBASE_PERF_CONNECTION` is set.

Coverage measures the code we wrote, leaving out migrations and generated code. It uses Microsoft's collector rather
than coverlet, because Windows Smart App Control blocks the rewritten copy of `Linkbase.dll` that coverlet produces.

## Importing existing data

Migration jobs are console commands of the app, so they need no UI and are easy to re-run. They run
instead of the web app. Re-importing is safe: rows already imported are recognised and skipped.

Put the UKC export at `data/samples/import.csv`, the Mountain Project ticks at `data/samples/ticks.csv`, and your
corrections at `data/corrections.json`. All are git-ignored. `docs/history/import-mapping.md` describes the mapping.

| Task | Command |
| --- | --- |
| Import both files and derive trips | `dotnet run --project src/Linkbase --launch-profile http -- climbing import <model> all` |
| Import one file | `dotnet run --project src/Linkbase --launch-profile http -- climbing import <model> ukc [path]` (or `mp`) |
| Rebuild the logbook from the files alone | the same with `--fresh --same-database`, which empties the model's records first |

`<model>` is the model's key under `Models`, such as `climbing`, or `default` when none are configured. Trips follow from the
ascents, and each file is one import batch that can be undone.

## Importing from web pages

The Import page adds crags and routes from an article or list. Paste the text, or give it an address to fetch. Pasting is
the more reliable option, and the right one for sites that block fetching or forbid scraping. Claude extracts the facts,
and you check, edit and match them before anything reaches your logbook. An import can be undone later.

Only facts and a brief summary are stored, never the page's own text. Extraction needs `Anthropic:ApiKey` (see Configuration).

To try the whole flow without a key or any API cost, run the app with canned results, then paste the text of
`tests/Linkbase.Tests/Fixtures/web/crag-article.txt`:

```powershell
dotnet run --project src/Linkbase -- --Extraction:Fixture=tests/Linkbase.Tests/Fixtures/web/crag-article.extraction.json
```

## Running in a container

The `Dockerfile` builds the image a platform runs. It works with Docker, Podman, Buildah and Kaniko, and under OpenShift's
restricted policy: any user id, a read-only root filesystem with `/tmp` mounted, port 8080. Logs are JSON on stdout, the
probes are `/health/live` and `/health/ready`, and everything is configured by environment variable (see
[operations](docs/operations.md#configuration)).

```powershell
docker build -t linkbase .
docker run --rm -e ConnectionStrings__Linkbase="<connection string>" linkbase model migrate
docker run -p 8080:8080 -e ConnectionStrings__Linkbase="<connection string>" linkbase
```

Migrations are that separate step, never part of start-up. To run the image against the compose database, use the `app`
profile, which `docker compose up` leaves out:

```powershell
docker compose --profile app run --rm app model migrate
docker compose --profile app up -d --build
```

CI builds the image on every push, scans it with Trivy and keeps an SBOM. `.gitlab-ci.yml` does the same on a
self-hosted GitLab without Docker-in-Docker: tests against a `services: postgres` container, the image built with
Buildah and pushed to the project's registry, then scanned. See [the deployment plan](docs/deployment-plan.md) for
Kubernetes and OpenShift.

## Graph explorer

The Explore page draws with Cytoscape.js and its fcose layout, which come with the app in `src/Linkbase/wwwroot/lib`,
each in a folder named with its version, beside its MIT licence. So Explore works offline and depends on no third party.

To update one, download the new version's file and licence from `https://cdn.jsdelivr.net/npm/<package>@<version>/`,
check the file against the SHA-256 that `https://data.jsdelivr.com/v1/packages/npm/<package>@<version>?structure=flat`
lists for it, put both in a new versioned folder, and change its path at the top of `src/Linkbase/wwwroot/js/graph.js`.
fcose needs layout-base and cose-base at the versions its own package names.

## Configuration

| Key | Purpose | Where |
| --- | --- | --- |
| `ConnectionStrings:Linkbase` | Postgres connection | `appsettings.Development.local.json` or environment variable |
| `Anthropic:ApiKey` | Reading text with AI | user secrets or environment variable |
| `Anthropic:Model` | Model for extraction | `appsettings.json` |
| `Extraction:Enabled` | Set to `false` to hide every import that sends text to Anthropic | `appsettings.json` or environment variable |
| `Extraction:Fixture` | Canned extraction from a file, instead of calling the API | `linkbase-fixture-extraction` launch config or environment variable |

Environment variables use a double underscore, for example `ConnectionStrings__Linkbase`. See
[operations](docs/operations.md#secrets) for how to list secrets, and how to get each one back if it's lost.

## Layout

```
Linkbase.sln
src/Linkbase/
  Components/   App shell, layout, and shared components
  Core/         Generic: the graph queries, Explore, models, record pages, CSV import and IDomainModule
  Data/         The one DbContext, entities and migrations
  Domains/      RegisteredDomains, and one folder per domain
    Climbing/
      Data/     Repositories and services
      Features/ One folder per feature: Ascents (the Logbook page), Home, Insights, Admin
      Import/   Parsers, corrections, trip rules and admin commands
tests/Linkbase.Tests/
data/samples/   Your export files (git-ignored)
docs/           Index in docs/README.md: models, the security roadmap, operations, performance and querying Postgres
models/         Model files and their CSVs, such as the example consultancy and the demo climbing logbook
scripts/        Local setup, backup, restore and recovery rehearsal
backups/        Database backups (git-ignored)
```

Folders are added as each phase needs them. See [CLAUDE.md](CLAUDE.md) for the architecture rules.
