# Operations

How to back up, restore and rebuild the database. Everything runs from the repo root with Docker running.

## What protects what

| Kept in | Holds | Backed up by |
| --- | --- | --- |
| Postgres (`linkbase_linkbase-data` Docker volume) | Everything the app shows, including ascents added or edited in the app, saved views and staged imports | `backup-db.ps1` |
| One Postgres database for each model made with **New model** (`model_<name>`, listed in the `user_model` table of the default database) | The records of that model | Nothing yet. `backup-db.ps1` covers the default database only, so dump these by hand with `pg_dump` |
| `data/samples/import.csv`, `ticks.csv`, `data/corrections.json` (git-ignored) | The source for a rebuild, and the only copy of the corrections | `backup-db.ps1` copies them |
| `.env`, `appsettings.Development.local.json` and user secrets (git-ignored) | Database password, API keys | Nothing. `init-dev.ps1` makes a new database password, and the Anthropic key has to be added again |
| Git | The code | A remote, once one is set up. There isn't one yet |

Everything is on one PC until a backup is copied off it.

## Backing up

```powershell
./scripts/backup-db.ps1
```

This writes `backups/<date-time>/`, holding `linkbase.dump` and the three source files. It only reads from the database, so
the app can stay running. `backups/` is git-ignored.

**A backup in `backups/` doesn't survive losing the PC.** Copy the folder somewhere else, such as private cloud storage or
an external drive. It holds your notes and partners' names, so keep it private. Nothing runs this on a schedule: back up
after adding a batch of ascents, and before anything risky such as a migration.

## Restoring

Stop the app, then:

```powershell
./scripts/restore-db.ps1 -Backup backups/<date-time>
```

It asks you to type `RESTORE`, backs up the current database first, and restores in one transaction, so a failed restore
changes nothing. If the app is newer than the backup, apply the newer migrations afterwards:

```powershell
dotnet ef database update --project src/Linkbase
```

To restore on a new PC: install the prerequisites, run `dotnet tool restore` and `./scripts/init-dev.ps1`, run
`docker compose up -d --wait`, then restore. Copy the source files back to `data/samples/` and `data/corrections.json`.

## Rebuilding from the export files

Use this if there's no backup, or to start again from the source files. Stop the app. **The first command deletes the
database.** Take a backup first if there's anything in it you want.

```powershell
docker compose down -v
docker compose up -d --wait
dotnet ef database update --project src/Linkbase
dotnet run --project src/Linkbase -- climbing import default all
```

The import also needs `data/corrections.json`, and stops if it's missing. `docker compose down` without `-v` only stops
Postgres and keeps the data.

A rebuild brings back everything in the export files, and nothing else:

| Comes back | Lost |
| --- | --- |
| Ascents, routes, crags, areas, partners and refs from the two CSVs | Anything added or edited in the app, including committed text imports |
| Trips, rebuilt from the ascents and `data/corrections.json` | Saved views |
| | Staged, failed and undone text imports |

Row ids can change, so a saved view from before a rebuild won't line up with the new rows. Prefer a restore when you have
a backup.

## Checking that recovery works

```powershell
./scripts/rehearse-recovery.ps1
```

This takes a fresh backup, or uses one you name with `-Backup`, then works in a throwaway container that it always removes.
It only reads from the real database.

1. It restores the backup. Every table's row count and every ascent must match the real database.
2. It rebuilds from the export files. The climbing tables and every ascent must match, and it lists what a rebuild loses.

It exits non-zero if anything differs. Once ascents have been added or edited in the app, step 2 will report differences,
which is correct: they show what a rebuild would lose, and that only a backup keeps them.

Last run on 4 November 2026, against a logbook of a few thousand ascents: the restore took about 1 second and the rebuild about 35 seconds, and
both matched exactly.

The restore into the real database hasn't been run for real, on purpose. The rehearsal runs the same script against the
throwaway container, which skips the prompt and the safety backup.

## Configuration

Everything is set by environment variable in a container, or locally by `appsettings.Development.local.json` (git-ignored, for the main database), user secrets and `appsettings.Development.json`.
A `:` in a setting's name becomes `__` in a variable, so `ConnectionStrings:Linkbase` is `ConnectionStrings__Linkbase`.

| Variable | What it does | Default |
| --- | --- | --- |
| `ConnectionStrings__Linkbase` | The main database. A secret | None, so required |
| `Models__<key>__Name`, `__ConnectionString`, `__Domains__0`, `__Order` | The models in the switcher, as in [models](models.md#configuring-models) | One model on the main database |
| `ConnectionStrings__<name>` | Each other model's database. A secret if it has a password | None |
| `Anthropic__ApiKey` | Reading text with AI. A secret | None, so text can't be read |
| `Anthropic__Model` | The model that reads text and answers questions | In `appsettings.json` |
| `Llm__Provider`, `Llm__BaseUrl`, `Llm__Model`, `Llm__MaxTokensField`, `Llm__Strict` | Use an OpenAI-compatible server instead of Anthropic, as in the [README](../README.md#configuration) | `anthropic` |
| `Llm__ApiKey` | The key for that server, if it wants one. A secret | None, so no key is sent |
| `Extraction__Enabled` | `false` hides every import that sends text to the model provider, and Explore's Ask box | `true` |
| `Extraction__AllowPrivateAddresses` | `true` lets Read text fetch pages from private network addresses. By default it refuses them, and cloud metadata addresses, so a web address can't be used to reach the network the app runs in | `false` |
| `Extraction__Fixture`, `Ask__Fixture` | A file of canned results instead of calling the API, for trying the flow with no key. Not for production | None |
| `Models__<key>__Home__Hide__0` | A home-page panel this model leaves out (`records`, `changes`, `imports`, `growth`) | None |
| `ASPNETCORE_ENVIRONMENT` | `Development` for plain-text logs and detailed errors | `Production` |
| `ASPNETCORE_HTTP_PORTS` | The port to listen on | `8080` in the image |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | Trust `X-Forwarded-Proto` and `-For` from the Route or ingress in front | `true` in the image |
| `HttpsRedirection` | Redirect HTTP to HTTPS. Off behind a proxy that ends TLS | `true`, `false` in the image |
| `Logging__Console__FormatterName` | `json` for one JSON object per line on stdout, or `simple` | `json`, `simple` in Development |
| `Logging__LogLevel__Default` | How much to log | `Information` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Send OpenTelemetry traces, metrics and logs to this collector, such as `http://otel-collector:4317`. Logs still go to stdout too | None, so nothing is sent. The Azure demo gets it from the Container Apps managed agent, which forwards traces and logs to Application Insights |
| `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_RESOURCE_ATTRIBUTES` | The standard OpenTelemetry settings, such as `http/protobuf` or an API key header | gRPC, none |
| `OTEL_SERVICE_NAME` | The service name telemetry carries | `linkbase` |
| `Docs__Pages__0`, `Docs__Pages__1`, … | Repo documents to show in the app, by file name without `.md` (the demo sets `requirements`). Each gets a rail link and a page at `/docs/<name>`. Only listed names are read | None, so there is no docs page |
| `Docs__Path` | The folder holding those documents | `docs` beside the app (the image has the requirements there), else the repo's `docs` |
| `Security__Identity` | Where the current user comes from: `configured` (the user in `Security__User`), `principal` (whoever ASP.NET Core authentication has signed in; the deployment adds the scheme) or `demo` (a click-to-sign-in page with no password, for made-up data only: see below) | `configured` |
| `Security__User` | With `configured` identity, who the app treats as signed in. Must be a user in the model file's `security.users`, or hold grants from a directory | None, so a model with row security on shows nothing |
| `Security__UserClaim` | With `principal` identity, the claim holding the user's id, such as `preferred_username` or `employee_id` | The first of `preferred_username`, name, `name`, name identifier and `sub` |

### Row security and the database

Turning row security on (`model security on`) creates a `linkbase_app` role in the database's cluster, with no login, and
grants it to the connecting user. The app runs each viewer's queries as that role (`SET ROLE`), which the policies bind;
the connecting user owns the tables, bypasses the policies, and so does `pg_dump`. Back up and restore as the owner, never
as `linkbase_app`. Roles belong to the cluster, so a restore into a new server needs `model security on` again (or the
`create role`), and `model security off` leaves the role in place. Backups hold every record whatever its label, so encrypt them.

The keys that sign the app's cookies and anti-forgery tokens are in the default model's database, in `data_protection_key`,
so restarts don't sign anyone out and every pod shares them. They're stored unencrypted, so treat database access as
access to them; encrypting them with a certificate given to every pod is possible later.

`Security__Identity=demo` (set in `appsettings.Demo.json`, used by `docker-compose.demo.yml`) shows row security to colleagues:
open `/signin`, pick a person, and compare what each sees. It has **no password**, so anyone who reaches the host can be
anyone. When hosting it, put the host behind its own sign-in (Entra "require authentication") or an IP allowlist, and load
only made-up data. [deploy/azure](../deploy/azure/README.md) does this on Azure with an IP allowlist. The consultancy model sets `Models__consultancy__RowSecurity=true`
(`"RowSecurity": true` in its entry under `Models`), so `model setup` turns row security on after loading it. Real sign-in
needs `principal` and an authentication package, which this repo doesn't ship. With `principal`, the app runs
authentication middleware if the deployment has registered a scheme. In the default `configured` mode nobody signs in, and
outside Development the app logs a warning saying so: keep it behind something that decides who may open it.

Responses carry `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` and a
Content-Security-Policy that only forbids framing, a changed base address, plugins and forms posting elsewhere. Add a
host allowlist (`AllowedHosts`) for the hostnames you serve, since the image ships with `*`. A deployment that makes outbound calls
should also have an egress policy: the app's own check only covers page fetches.

The app writes logs only to stdout, and files only to `/tmp`. It runs at the root of its host, so give it its own hostname
rather than a path such as `/linkbase`.

| Endpoint | Says | Use it for |
| --- | --- | --- |
| `/health/live` | The process is up. Checks nothing else | Liveness probe |
| `/health/ready` | Every model's database is reachable and migrated | Readiness and startup probes |

## Secrets

Secret values are never in the repo, and backups don't include them. This is the record of which ones exist and how to get
each one back.

| Key | What it's for | Needed when | If you lose it |
| --- | --- | --- | --- |
| `ConnectionStrings:Linkbase` | The app's connection to Postgres | Always | Run `./scripts/init-dev.ps1`, which rebuilds it from the password in `.env` |
| `Anthropic:ApiKey` | Reading text with AI | Only for reading text | Create a new key in the Anthropic Console, because existing keys can't be viewed again |
| `Llm:ApiKey` | Reading text through an OpenAI-compatible server | Only with `Llm:Provider` set to `openai`, and only if that server wants a key | Create a new key with that provider |
| The setting a model's `tokenSetting` names, such as `Security:DirectoryToken` | A bearer token for the directory that holds people's security grants | Only for a security part whose source is `api` | Ask the directory's owner for a new one |

`POSTGRES_PASSWORD` in `.env` is the other secret. It sets the password when the database volume is first created.

The Azure demo keeps its secrets in a Key Vault instead, by these names: `anthropic-api-key`, `postgres-password` and the
four `cs-*` connection strings. `deploy/azure/deploy.ps1` writes them, and `-ReplaceAnthropicKey` changes the Anthropic one.

To see which secrets are set, without printing their values:

```powershell
dotnet user-secrets list --project src/Linkbase | ForEach-Object { $_ -replace ' = .*$', '' }
```

To set or remove one:

```powershell
dotnet user-secrets set Anthropic:ApiKey "<your key>" --project src/Linkbase
dotnet user-secrets remove Anthropic:ApiKey --project src/Linkbase
```

Plain `dotnet user-secrets list` prints the values, so don't paste its output anywhere.

Things worth knowing:

- **Where they live:** `%APPDATA%\Microsoft\UserSecrets\b25134c3-dbf8-4dda-adbb-c5b225737b25\secrets.json`, in plain text,
  outside the repo. The id comes from `src/Linkbase/Linkbase.csproj` and isn't itself secret.
- **Per Windows account:** a program running under another profile sees a different, possibly empty, store. That's what
  "The ConnectionString property has not been initialized" means.
- **`.env` and an existing volume:** Postgres only reads the password when the volume is first created. If `.env` is lost
  while the volume is kept, a new password won't match and connections will fail. Restore `.env`, or back up from inside
  the container (`backup-db.ps1` doesn't need the password), then recreate the volume and restore.
- **Development only:** ASP.NET Core only loads user secrets, and the app only loads `appsettings.Development.local.json`, in the Development environment. Anywhere else, set environment
  variables such as `ConnectionStrings__Linkbase` and `Anthropic__ApiKey`.
- **Keeping them safe:** keep the Anthropic key and `.env` password in a password manager. Don't put them with the backups.

## From before the rename

On 7 November 2026 the app was renamed from Logbook to Linkbase, and the database moved with it: backed up, restored into
the new `linkbase-db-1` container with a `linkbase` database and user, and checked table by table. The old container, its
volume and the old `ConnectionStrings:Logbook` secret have since been removed. The backup taken just before the move is
`backups/20261107-143137`, and `restore-db.ps1` still reads backups from before the rename, which hold `logbook.dump`.

The sample container was renamed in place to `linkbase-samples`, with its role renamed to `linkbase` and `example` to
`consultancy`. An unused 7 GB copy of the Swiss data from before models had their own databases was dropped.

## Still to do

- Choose where backups are copied to, and consider scheduling them.

## Migration history

Migrations were squashed into one `InitialCreate` on 2026-11-10, 2026-11-13 and 2026-11-15. A database made before the latest
squash has the old migration names in `"__EFMigrationsHistory"`, so `dotnet ef database update` or `model migrate` fails with
"already exists", such as `function "graph_search_text" already exists`. The 2026-11-15 squash changed no schema (only column
order within three tables differs from a database built by the old migrations), so a database that had every earlier migration
applied, up to `UserModels`, only needs its history pointed at the baseline, with the id of the single file in
`src/Linkbase/Data/Migrations/`:

```sql
BEGIN;
DELETE FROM "__EFMigrationsHistory";
INSERT INTO "__EFMigrationsHistory" VALUES ('20261115053007_InitialCreate', '10.0.14');
COMMIT;
```

Run it once in every model's database (back up the real one first). The same applies to a backup taken after `UserModels` was
applied: restore it, then run the SQL above. A database older than that is behind the schema and can't be moved this way:
restore it, run `dotnet ef database update` with the previous commit's migrations first, then reset the history.
