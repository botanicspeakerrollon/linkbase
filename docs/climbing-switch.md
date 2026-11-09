# Switching the real logbook to the generic store

Step 6 of [the plan](climbing-to-generic-plan.md). Your real logbook is still in the typed tables (`area`, `crag`, `route`,
`ascent`, `partner`, `ascent_partner`, `trip`). This moves it into the generic store and then drops the typed tables.

The code for the second half is on the branch `climbing-generic-step6-switch`, not on `main`, so that nothing drops a table
before the data has been copied. Its migration also refuses to run until the copy is there (see below).

**The order matters.** Do the steps in sequence, on this PC, with the app stopped except where a step says otherwise. Nothing
here has been run against your real database yet: the sandbox I work in can't reach it.

## 1. Back up and prove the backup works

```powershell
./scripts/backup-db.ps1
./scripts/rehearse-recovery.ps1
```

Copy the new folder in `backups/` off this PC. The rehearsal restores it into a throwaway container and must say
`Recovery works.` Run it from `main`, which still knows the typed tables.

## 2. Copy the logbook into the generic store

On `main`, with the app stopped (`dotnet run` holds the Debug build open, so stop it or use `-c Release`):

```powershell
dotnet run --project src/Linkbase -- climbing to-generic climbing --same-database
```

`climbing` is the model's key under `Models` in `appsettings.Development.json`. `--same-database` is needed because the
target is the database the typed tables are in. The command copies every record, link, external id and relationship count,
then checks the copy against the typed tables, and prints `... matches the typed tables.` If it prints differences instead, stop
and tell me what it says. It only reads the typed tables and can be repeated: each run replaces the generic store.

Until step 4 the Explore page will probably show records twice, once from each side, because its views still read both. The Logbook,
Insights and web import already read only the generic store. Open them and look at your own data.

Import batches made before this don't carry over. Undoing a web import made before the switch will no longer take its rows
back, because the copy doesn't mark them as that batch's. Imports from now on can be undone as before.

## 3. Check it

- Logbook: the number of ascents matches what you had, and a few you know well look right.
- Insights: the trips and partners tabs have your history.
- A trip: **Explore** one and look at its ascents.
- Optional: `dotnet run --project src/Linkbase -- climbing verify-generic climbing` repeats the check.

## 4. Take the typed tables away

Back up once more (`./scripts/backup-db.ps1`), then, with the app stopped:

```powershell
git merge climbing-generic-step6-switch
dotnet ef database update --project src/Linkbase
```

The migration is `SwitchClimbingToGenericStore`. It **can't be reversed**: its `Down` refuses, and the way back is restoring
the backup you just took. Before it changes anything it checks that each typed table that has rows has records of its type in
the generic store, and that old external ids have new ones. If either is missing it stops with a message saying to run step 2.
Then it points the graph views at the generic store only and drops the typed tables, their views and the old external id
columns.

If you run `model migrate` instead, it applies to every model, including the sample databases. That's harmless for them.

## 5. Afterwards

```powershell
./scripts/rehearse-recovery.ps1
```

This now compares the generic records. The restore must match exactly. The rebuild runs `climbing import default all --fresh
--same-database` into a throwaway database and compares every ascent; ascents added or edited in the app since the exports
will differ, which is correct.

You can drop the trial copy if you made one: `docker exec linkbase-samples psql -U linkbase -d postgres -c "DROP DATABASE
climbing_generic"`, and remove its entry from `appsettings.Development.json`.

## If something goes wrong

- Restore the backup: [operations](operations.md#restoring). Nothing in steps 1 to 3 changes the typed tables.
- The migration runs in one transaction, so a failure part-way leaves the database as it was.
