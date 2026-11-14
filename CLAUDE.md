# Linkbase

One ASP.NET Core app (Blazor Interactive Server + MudBlazor) and one Postgres database. If there's a
choice between clever and boring, choose boring. See `README.md` for commands.

## Architecture rules

- **Layout:** `Core/` is generic (graph, Explore, models, their generated record pages and CSV import) and knows no domain. `Domains/<Domain>/` is everything specific to one,
  plugged in through `IDomainModule` and listed in `RegisteredDomains`. `Data/` holds the one `DbContext`, the entities and
  the migrations. Entities keep the `Linkbase.Data` namespace, because the migration snapshot records it.
- **Data access:** all database access goes through repository or service classes with
  named methods (`AscentsSinceAsync`, `FindCragCandidatesAsync`, `UpsertRouteAsync`). Razor components
  never touch `DbContext`.
- **Context lifetime:** use `IDbContextFactory<LinkbaseDbContext>` with a short-lived context per
  operation. Never inject a scoped `DbContext` into components; it would live for the whole circuit.
- **Loading:** no lazy loading (don't add the proxies package). Prefer projecting to DTOs with `Select`,
  and use `AsNoTracking()` for reads.
- **DTOs:** repositories return DTO records, never entities, and projections name each field explicitly.
  The future field-level security work depends on this.
- **Raw SQL:** graph and recursive queries may use raw SQL via `FromSql` / `SqlQuery` with keyless types.
- **Bulk writes:** one transaction per import batch.
- **Graph views:** build `props` in `graph_nodes` / `graph_edges` from named columns, never `to_jsonb(row)`.
- **Sensitive fields:** keep them in typed columns, not inside JSON. See `docs/security-roadmap.md`.
- **History:** code that adds, changes or removes records or links writes the audit trail in the same transaction, through
  `ChangeRecorder`. A hand edit needs a reason unless the model file says `history.requireReason: false`. See
  `docs/history/audit-history-plan.md`; `ChangeRecorderCoverageTests` fails when a new write path forgets it.
- **Copyright:** store facts (names, grades, lengths, locations) and the source URL. Never store long
  descriptions or guidebook text verbatim; summaries are brief and paraphrased.

## Schema conventions

- Tables, columns, keys and indexes are snake_case, applied in `LinkbaseDbContext.ApplyDatabaseConventions`.
- Enums are stored as snake_case strings via `SnakeCaseEnumConverter<T>`. Register new enums in
  `ConfigureConventions`.
- Foreign keys never cascade. Deleting something that's still referenced fails, and services report that.
  `SaveChanges` wraps the error in `DbUpdateException`; `ExecuteDelete` throws `PostgresException` directly.
- `external_ref` points at a node (`node_id`) and has no foreign key, so code that deletes a node must delete its refs in
  the same transaction (`ExternalRefRepository.RemoveForNodesAsync`).
- `link_definition.links` counts each relationship's links, because counting them live is too slow on large models. Code
  that adds or removes rows in `node_link` updates it in the same transaction.
- `created_at` equals `updated_at` until a row is edited; import undo relies on this. `SaveChanges` stamps
  them, but `ExecuteUpdate` bypasses it, so set `UpdatedAt` explicitly there.

## Working style

- Small commits, one per meaningful step.
- Be prepared to work in a new worktree or branch if other work is happening. Be aware of merge conflicts and the need to collaborate with other work.
- User facing and repo docs must be checked for accuracy against new work, and updated if required. 
- Once new work is completed, it must be merged to main, but not pushed to remote.
- The repo is anonymous: nothing in it may identify its owner. Commits use the repo's own `Linkbase` identity and are made
  with `TZ=UTC0` (`$env:TZ = 'UTC0'` in PowerShell), or git records the PC's time zone. Test data uses made-up people,
  places and dates, never anything from the real logbook.
- Build, run the tests and run the app before saying something works. Warnings are errors.
- Database tests use Testcontainers against real Postgres and carry `[Trait("Category", "Database")]`.
  Mock the LLM client; tests never hit the API.
- Page tests extend `UiTestBase` (bUnit, real repositories). Anything about layout, real mouse events or `graph.js` goes
  in `BrowserTests` (Playwright, `[Trait("Category", "Browser")]`), which can only reach the test database.
- Smart App Control is on, and it blocks coverlet's rewritten DLL. Measure with `coverage.runsettings`, which works in
  memory, and don't try to get around the block.
- Backup, restore and rebuild are in `docs/operations.md`. Test recovery with `scripts/rehearse-recovery.ps1`, which only
  reads the real database. Never run `docker compose down -v` or restore into the real database unless asked.
- A new secret gets a row in the Secrets table in `docs/operations.md`, by name only. Never write a secret's value into a
  committed file (`appsettings.Development.local.json` is git-ignored and holds the database one), and never run plain `dotnet user-secrets list`, which prints values.
- Models: each has its own database, listed under `Models` and switched in the rail. A generic model comes from a model file
  (`models/`) and lives in the generic store (`node`, `node_link` and the definition tables). See `docs/models.md`. Climbing is
  the one domain model. `model migrate` migrates every model, including the real one, so ask before running it.
- The large sample is the Swiss public transport model, in the `swiss_rail` database of the `linkbase-samples` container. Check
  query changes against it (`docs/performance.md`). Queries sorting `graph_nodes` with a limit must find their rows first, or
  the planner may walk the whole graph.
- Ask before adding a NuGet dependency, and ask when requirements are ambiguous, especially about source
  data formats. Inspect real export files before writing a parser.
- A domain's pages live in `Domains/<Domain>/Features/<Feature>/`.
- The UI is dark only. Colours live in `AppTheme.cs` and reach the pages as `--mud-palette-*` variables, so don't
  hard-code them. Inputs are `Variant.Outlined` and `Margin.Dense`, because v9 has no global default for these.
- MudBlazor is v9, and its analyzer turns removed attributes into build errors. Headings use `HtmlTag`, not
  `Component`. Selects over enums need `ToStringFunc`, or the chosen value shows as the raw enum name.
- Trips are derived. `ClimbingRecordHook` rebuilds them when an ascent's date or country changes, and hand edits to a
  trip won't survive a rebuild. Multi-country merges come from `data/corrections.json`.
- EF can't sort after projecting to a positional record, so order before the `Select`.
- The Explore page owns the visible graph, and `wwwroot/js/graph.js` only draws it and reports clicks. Nodes already
  on the canvas must never move when others are added. `restorePinned` enforces this, because fcose alone doesn't.
  Dragging is the one exception: links act as springs, up to `dragHops` away. How it feels is set by the constants at
  the top of `graph.js`.
- Reading text treats the model's answer as untrusted. `ModelExtractionValidator` drops any name or value not in the source
  text, and a domain's `IRecordHook.CheckExtracted` drops values it can tell are wrong. Keep that check, and never store the
  page's own text in `raw_payload`. A domain adds to the prompt with `IRecordHook.ExtractionNotes`.
- The extractor uses `tool_choice` auto with a strict tool, because `claude-sonnet-5-5` and newer reject forced tool
  choice. Tests use a fake `IModelExtractor`; `Extraction:Fixture` gives the same without an API key.
- Climbing has no web import of its own: it reads text through `Core/Extraction`, and `ExistingRecordMatcher` decides what is
  already there. Undo keeps anything edited since, using `updated_at`, so it keeps a record that something now needs.
- If `dotnet run` is holding the Debug build open, build, test and run with `-c Release` instead, using the
  `linkbase-side-by-side` launch config on port 5174. Never stop a `dotnet run` you didn't start.
