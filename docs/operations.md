# Operations

How to back up, restore and rebuild the database. Everything runs from the repo root with Docker running.

## What protects what

| Kept in | Holds | Backed up by |
| --- | --- | --- |
| Postgres (`linkbase_linkbase-data` Docker volume) | Everything the app shows, including ascents added or edited in the app, saved scenes, duplicate decisions and staged imports | `backup-db.ps1` |
| `data/samples/import.csv`, `ticks.csv`, `data/corrections.json` (git-ignored) | The source for a rebuild, and the only copy of the corrections | `backup-db.ps1` copies them |
| `.env` and user secrets (git-ignored) | Database password, API keys | Nothing. `init-dev.ps1` makes a new database password, and the Anthropic key has to be added again |
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
dotnet run --project src/Linkbase -- import all
```

`import all` also needs `data/corrections.json`, and stops if it's missing. `docker compose down` without `-v` only stops
Postgres and keeps the data.

A rebuild brings back everything in the export files, and nothing else:

| Comes back | Lost |
| --- | --- |
| Ascents, routes, crags, areas, partners and refs from the two CSVs | Anything added or edited in the app, including committed web imports |
| Trips, rebuilt from the ascents and `data/corrections.json` | Saved scenes |
| Possible duplicates, flagged again as open | Decisions on those duplicates |
| | Staged, failed and undone web imports |

Row ids can change, so a saved scene from before a rebuild won't line up with the new rows. Prefer a restore when you have
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

Last run on 4 November 2026, against 2,318 ascents: the restore took about 1 second and the rebuild about 35 seconds, and
both matched exactly.

The restore into the real database hasn't been run for real, on purpose. The rehearsal runs the same script against the
throwaway container, which skips the prompt and the safety backup.

## Secrets

Secret values are never in the repo, and backups don't include them. This is the record of which ones exist and how to get
each one back.

| Key | What it's for | Needed when | If you lose it |
| --- | --- | --- | --- |
| `ConnectionStrings:Linkbase` | The app's connection to Postgres | Always | Run `./scripts/init-dev.ps1`, which rebuilds it from the password in `.env` |
| `Anthropic:ApiKey` | Web import's extraction | Only for web import | Create a new key in the Anthropic Console, because existing keys can't be viewed again |
| `Neo4j:Password` | Reading the old Neo4j graph | Only for `inspect neo4j`, `export neo4j`, and until Neo4j is retired | Reset it in Neo4j. You can delete it once Neo4j is gone |

`POSTGRES_PASSWORD` in `.env` is the other secret. It sets the password when the database volume is first created.

To see which secrets are set, without printing their values:

```powershell
dotnet user-secrets list --project src/Linkbase | ForEach-Object { $_ -replace ' = .*$', '' }
```

To set or remove one:

```powershell
dotnet user-secrets set Anthropic:ApiKey "<your key>" --project src/Linkbase
dotnet user-secrets remove Neo4j:Password --project src/Linkbase
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
- **Development only:** ASP.NET Core only loads user secrets in the Development environment. Anywhere else, set environment
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
