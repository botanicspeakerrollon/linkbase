# Moving climbing into the generic store

Climbing is the one model with its own typed tables. Every other model lives in the generic store (`node`, `node_link` and
the definition tables). This plan moves climbing into the generic store too, so there's one way to store, import, show and
edit data, and climbing becomes a model file plus a small module of behaviour.

It replaces the "climbing stays as typed tables" choice in [extensibility-plan.md](extensibility-plan.md).

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
| `review_item` (possible duplicates) | Removed beforehand | — |
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
| `ReconciliationService`, the Neo4j comparison | Removed beforehand |

### Pages

| Today | After |
| --- | --- |
| Logbook: a grid with filters and a summary | A module page over the generic store. Later it could be a generic list with sorting and filters (G8) |
| Add or edit an ascent, creating a route, crag and area inline | A module page that writes through `RecordRepository`. Later it could be a generic form that can create linked records (G7) |
| Browse: area tree, crag and route pages, dialogs | **Removed**, in favour of the generic record pages and Explore. To decide: keep the area tree as a module page? |
| Insights: ten SQL reports | A module page with its queries rewritten over `node` and `node_link`. Replaced later by saved searches and dashboards (G10) |
| Climbing home page stats | Generic home page stats, as the other models have |
| Web import (`/import`, staging, review, undo) | A module page on generic staging (G9), then made generic for any model |
| Duplicates (`/admin/review`) | Removed beforehand |

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
| G4 | **Module hooks and module-owned fields**: code runs when a type's records are saved or deleted, and can keep fields people can't edit | M | Grade rank, refreshing trips |
| G5 | **Module-owned records**: types only the module writes, shown read-only and marked as derived | M | Trips |
| G6 | **External refs by node id**: `external_ref` points at nodes, not typed rows | S | UKC and MP ids, web import sources |
| G7 | **Creating linked records from a form**: pick a route, or create one, and its crag, without leaving the page | M–L | Later: replaces the custom Add ascent page |
| G8 | **Sorting and filtering record lists by field** | M | Later: replaces the custom Logbook page |
| G9 | **Generic import staging**: stage, review, commit and undo, for any import | L | Web import. Also needed for generic web import |
| G10 | **Saved searches and dashboards** | L | Later: replaces Insights. Belongs with multi-user |

Only G1 to G6 are needed for the move. G7 to G10 replace module pages later, and each is on the roadmap for its own sake.

**Performance.** Values are JSON, so a filter on a field is a JSON lookup. That's nothing at a personal logbook's size.
`swiss_rail` already shows the store copes with millions of rows. If a field is filtered often, an expression index on it is
the fix.

## Order of work

Each step leaves the app working, and the real data isn't touched until step 6.

0. **Delete what's going anyway.**
   - Neo4j: the driver package, the exporter, inspector and comparison, their commands, tests and docs, and the `neo4j`
     source value. The real database has no rows using it.
   - The Duplicates page, `review_item`, and the import step that flags duplicates.
1. **Generic features for storage: G1 to G6.** Each one is useful to model files straight away.
2. **The climbing model file and a converter.**
   - `climbing to-generic` copies the typed tables into a model's generic store.
   - It's checked against the typed tables: counts per type, every ascent's fields, links and refs.
   - It's loaded into a second, local database, shown in the rail as "Climbing (generic)". Explore, the record pages, CSV
     import and export work on it at once.
3. **Module behaviour on the generic store.** Grade rank, trips with `corrections.json`, captions. A rebuilt trip list must
   match today's.
4. **Port the module pages, one at a time.** Logbook, Add ascent, Insights and web import read and write the generic store.
   Browse goes.
5. **Imports.** The UKC and MP parsers write through the generic import path. `import all` rebuilds a fresh climbing model on
   demand, and an end-to-end test runs it on made-up export files in CI.
6. **Switch.**
   - Back up first.
   - With your go-ahead, convert the real data and verify it with the rehearsal script.
   - Point the climbing model at the generic store, and keep the old tables read-only for a while.
   - Then a migration drops them, along with the climbing entities and enums. The graph views read only the generic store.

## Decisions to make

- **Browse.** Remove it, or keep the area tree as a module page?
- **Route descriptions.** Drop the field? No route has one.
- **Demo.** A made-up climbing logbook can be generated once step 2 exists, the same way the consultancy sample is. The
  real logbook can't be the demo, because the repo must stay anonymous.
- **Field-level security.** Still unsolved for the generic store (see [security-roadmap.md](security-roadmap.md)). This
  move doesn't make it worse for customers, but climbing loses its typed columns.
