# Moving climbing into the generic store

Climbing is the one model with its own typed tables. Every other model lives in the generic store (`node`, `node_link` and
the definition tables). This plan moves climbing into the generic store too, so there's one way to store, import, show and
edit data, and climbing becomes a model file plus a small module of behaviour.

It replaces the "climbing stays as typed tables" choice in [extensibility-plan.md](extensibility-plan.md).

## Where we are

*Read-out on 2026-11-09.*

| Step | State |
| --- | --- |
| 0. Delete Neo4j and Duplicates | Done |
| 1. Generic features G1 to G6 | Done |
| Later features G7, G8, G9 | Done: forms that create linked records, sorting and filtering, staged imports |
| 2. Climbing model file and converter | Done: `models/climbing/model.json`, `climbing to-generic` and `climbing verify-generic`. Tested on made-up data; **not yet run on the real logbook**, which needs your database. See [models](models.md#climbing-in-the-generic-store) |
| 3. Module behaviour on the generic store | Done: `ClimbingRecordHook` keeps `grade_rank`, names ascents after their routes and rebuilds trips; `GenericTripService` rebuilds the `Trip` records. A test checks that its trips match the typed tables' |
| 4. Port the module pages | Done: Browse and the typed Add ascent page are gone, the Logbook reads the generic store (`LogbookRepository`; its Add button is the generic form), Insights reads it through the `climbing_*` views (migration `ClimbingInsightsOnGenericStore`), and web import matches, commits and undoes on Area, Crag and Route records (`WebImportRepository`, keeping its own review screen). A route's summary is no longer kept, because the model has no description field |
| 5. Imports | Done: `GenericAscentImportService` loads the UKC and Mountain Project parsers' rows into the generic store as one undoable batch per file. `climbing import <model> ukc\|mp\|all [--fresh]` runs it, and a test checks it gives what the typed importer gives. `climbing verify-generic <model> --imported` checks an imported store against the typed tables, matching trips by key because they're numbered afresh. Run on the real logbook: matches. The typed importer stays until step 6 |
| 6. Switch | Done in code on this branch, not yet run on the real logbook. See [climbing-switch](climbing-switch.md). `SwitchClimbingToGenericStore` refuses to run until the logbook has been copied, then points the graph views at the generic store only and drops the typed tables. The typed entities, repositories, importer, trip service, converter and verifier are gone, and `rehearse-recovery.ps1` checks the generic records |

**Two gaps in step 3.**
- The hook only runs for a model that lists `Climbing` under `Domains`, and that also turns on the module's pages, which still
  read the typed tables. So "Climbing (generic)" is configured without the module for now, and the hook is covered by tests
  rather than by the running app. It joins the model when step 4 has ported the pages.
- An ascent's caption was already supplied by the module (`LabelStyles` and `LabelsNamedAfterParent`), not by a check in Core,
  so there was nothing to remove there.

**What's next.**
1. ~~Decide on **Browse**.~~ Removed. The pages, their dialogs and tests are gone, along with the nav item. The module no longer
   links to crag and route pages: the Logbook, Insights and web import review show names as plain text, and Explore has no
   "Open and edit" button for areas, crags and routes. Both come back when step 4 moves the pages onto the generic record pages
   (`/data/{Type}/{Key}`).
2. Run `climbing to-generic climbing-generic` against your own logbook and read what `verify-generic` says. That shows whether
   the real data has anything the made-up data doesn't, such as a value outside a choice field's options. Result: success. 
**Until step 6, the Logbook shows the generic store only.** On the real model, whose data is still in the typed tables, it says it has no ascents. Insights and web import, like the Logbook, read and write the generic store, so on the real model they are empty or write to the generic store until step 6.

3. Step 4, one page at a time, each leaving the app working: Logbook and Add ascent first, since the generic list (G8) and form
   (G7) can replace them, then Insights, then web import onto generic staging (G9). Add `Climbing` to the generic model's
   `Domains` as these land.
4. Step 5, the UKC and Mountain Project parsers writing through the generic import path.
5. Step 6, the switch: follow [climbing-switch](climbing-switch.md).

## Why

The split was kept for three reasons. None of them holds up any more:

| Reason given | Why it doesn't hold |
| --- | --- |
| Grade ranking and derived trips would be lost | They're logic, not storage. Module code can run them on generic nodes. |
| The migration was verified row by row | `scripts/rehearse-recovery.ps1` already compares a rebuild with the real data, table by table and ascent by ascent. The same check can verify this move. |
| Sensitive fields belong in typed columns | That has to be solved in the generic store anyway, because every customer's data lives there. Keeping climbing apart only protects the one model no customer uses. |

Meanwhile the split costs:
- **Two of everything:** two imports, two kinds of record page, two edit paths, and features built for one side only.
- **A demo that doesn't show the product:** the richest model shows code customers won't get.
- **Climbing leaking into everything else:**
  - every model's database carries climbing's empty tables;
  - the "an ascent's name is its route's" rule applies to any type called `Ascent`;
  - the graph views have to merge two sources.

## Where it ends up

**Storage is generic. Behaviour stays in the module.** A module keeps its own pages, imports and rules, and they read and write
the generic store. A module can't have tables of its own.

- `models/climbing/model.json` describes the types, fields and relationships.
- `Domains/Climbing` keeps:
  - grade ranking;
  - trip derivation;
  - the UKC and Mountain Project parsers;
  - web import;
  - the Logbook and Add ascent pages;
  - Insights, until saved searches replace it.
- Every generic feature works on climbing for free: record pages, CSV import, Explore, the table view and export.

## What climbing has, and where each piece goes

### Data

| Today | In the generic store | Needs |
| --- | --- | --- |
| `area` with `parent_id` and `kind` | `Area` nodes, `Area IN Area` links, `kind` field | Choice field (G1) |
| `crag`: name, rock type, lat, lon, notes | `Crag` node with those fields, `Crag IN Area` link | — |
| `crag.aliases` (a list, used to match web imports) | A list field | List field (G2) |
| `route`: name, grade, grade system, discipline, length, pitches | `Route` node, `Route AT Crag` link | Choice fields (G1) |
| `route.description` | Dropped. No route has one, and the copyright rule keeps it brief anyway | — |
| `ascent`: date, date precision, style, raw style, notes, rating, source | `Ascent` node, `Ascent OF Route` link | Choice fields (G1), a required route and date (G3) |
| `ascent_partner` | `Ascent WITH Partner` links | — |
| `partner` | `Partner` nodes | — |
| `trip` and `ascent.trip_id`, `trip_inferred` | `Trip` nodes, `Ascent ON_TRIP Trip` and `Trip VISITED Area` links, written only by the module. "Inferred" goes on the `ON_TRIP` link | Module-owned records (G5) |
| Grade rank, worked out in queries | A read-only `grade_rank` number, kept by the module | Module-owned fields (G4) |
| `external_ref`: UKC and MP ids and URLs, by entity type and row id | The same table, keyed by node id | External refs by node id (G6) |
| `import_batch` | Already shared with the CSV import | — |
| `review_item` (possible duplicates) | Removed in step 0 | — |
| `graph_scene` | Unchanged. Node ids stay `crag:12` and so on, so saved scenes still line up | — |

Node keys are today's row ids, and the relationship names are the ones Explore already shows (`IN`, `AT`, `OF`, `WITH`,
`ON_TRIP`, `VISITED`). So `crag:12` in the typed tables is `crag:12` in the generic store, and Explore, scenes and links all
keep working.

### Logic

| Today | After |
| --- | --- |
| `GradeRanking` | Unchanged. Runs when a route is saved and writes `grade_rank` (G4) |
| `TripBuilder` (pure logic) and `data/corrections.json` | Unchanged. `TripService` rewrites the `Trip` nodes and links instead of the `trip` table (G5) |
| `TripRefresher` called from `AscentRepository` | Called by a hook when an ascent's date or country changes (G4) |
| An ascent's caption is its route's name, by a check on the type name in Core | The climbing module supplies captions. Core loses the check |
| `ReconciliationService` (the counts report after an import) | Unchanged in purpose, counting nodes instead of rows. Its Neo4j comparison was removed in step 0 |

### Pages

| Today | After |
| --- | --- |
| Logbook: a grid with filters and a summary | A module page over the generic store. Later it could be a generic list with sorting and filters (G8) |
| Add or edit an ascent, creating a route, crag and area inline | A module page that writes through `RecordRepository`. Later it could be a generic form that can create linked records (G7) |
| Browse: area tree, crag and route pages, dialogs | **Removed**, in favour of the generic record pages and Explore |
| Insights: ten SQL reports | A module page with its queries rewritten over `node` and `node_link`. Replaced later by saved searches and dashboards (G10) |
| Climbing home page stats | Generic home page stats, as the other models have |
| Web import (`/import`, staging, review, undo) | A module page on generic staging (G9), then made generic for any model |
| Duplicates (`/admin/review`) | Removed in step 0 |

### Imports and operations

| Today | After |
| --- | --- |
| UKC and MP parsers, `import all` | Parsers unchanged. They write nodes and links through the generic import path, so batches and undo work as they do for CSV |
| `rehearse-recovery.ps1` | Compares the generic store with the typed tables during the move, then compares a generic rebuild with a backup |
| One `DbContext`, climbing entities in `Data/` | Climbing entities, their tables and their enums go once the move is done. The graph views read only the generic store |

## Generic features needed

Each of these is something customers will want too. That's the test for building it rather than special-casing climbing.

| | Feature | Size | Needed for |
| --- | --- | --- | --- |
| G1 | **Choice fields**: a fixed list of values in the model file, shown as a select, checked on save and import | S | Style, discipline, grade system, area kind, date precision, source |
| G2 | **List fields**: several text values, such as aliases | S | Crag aliases |
| G3 | **Required fields and links**: a record can't be saved without them | S–M | An ascent's route and date |
| G4 | **Module hooks and module-owned fields**: code runs when a type's records are saved or deleted, and can keep fields people can't edit. Done, see [models](models.md#owned-by-a-module) | M | Grade rank, refreshing trips |
| G5 | **Module-owned records**: types only the module writes, shown read-only and marked as derived. Done, see [models](models.md#owned-by-a-module) | M | Trips |
| G6 | **External refs by node id**: `external_ref` points at nodes, not typed rows. Done: a ref has `node_id` or the climbing columns, which go in step 6 | S | UKC and MP ids, web import sources |
| G7 | **Creating linked records from a form**: pick a route, or create one, and its crag, without leaving the page | M–L | Later: replaces the custom Add ascent page. Done |
| G8 | **Sorting and filtering record lists by field** | M | Later: replaces the custom Logbook page. Done |
| G9 | **Generic import staging**: stage, review, commit and undo, for any import | L | Web import. Also needed for generic web import. Done for CSV; the web import moves onto it later |
| G10 | **Saved searches and dashboards** | L | Later: replaces Insights. Belongs with multi-user |

Only G1 to G6 are needed for the move. G7 to G10 replace module pages later, and each is on the roadmap for its own sake.

**Performance.** Values are JSON, so a filter on a field is a JSON lookup. That's nothing at a personal logbook's size.
`swiss_rail` already shows the store copes with millions of rows. If a field is filtered often, an expression index on it is
the fix.

## Order of work

Each step leaves the app working, and the real data isn't touched until step 6.

0. **Delete what's going anyway.** Done.
   - Neo4j: the driver package, the exporter, inspector and comparison, their commands, tests and docs, and the `neo4j`
     source value. The real database has no rows using it.
   - The Duplicates page, `review_item`, and the import step that flags duplicates.
1. **Generic features for storage: G1 to G6.** Each one is useful to model files straight away. All done.
2. **The climbing model file and a converter.**
   - `climbing to-generic` copies the typed tables into a model's generic store.
   - It's checked against the typed tables: counts per type, every ascent's fields, links and refs.
   - It's loaded into a second, local database, shown in the rail as "Climbing (generic)". Explore, the record pages, CSV
     import and export work on it at once.
3. **Module behaviour on the generic store.** Grade rank, trips with `corrections.json`, captions. A rebuilt trip list must
   match today's.
4. **Port the module pages, one at a time.** Logbook, Add ascent, Insights and web import read and write the generic store.
   (Browse is already gone.)
5. **Imports.** The UKC and MP parsers write through the generic import path. `import all` rebuilds a fresh climbing model on
   demand, and an end-to-end test runs it on made-up export files in CI.
6. **Switch.**
   - Back up first.
   - With your go-ahead, convert the real data and verify it with the rehearsal script.
   - Point the climbing model at the generic store, and keep the old tables read-only for a while.
   - Then a migration drops them, along with the climbing entities and enums. The graph views read only the generic store.

## Decisions to make

- **Route descriptions.** Dropped from the model file, because no route has one. Say if that's wrong before step 6.
- **Demo.** A made-up climbing logbook can be generated once step 2 exists, the same way the consultancy sample is. The
  real logbook can't be the demo, because the repo must stay anonymous.
- **Field-level security.** Still unsolved for the generic store (see [security-roadmap.md](security-roadmap.md)). This
  move doesn't make it worse for customers, but climbing loses its typed columns.
