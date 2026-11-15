# Where things live

A map of the code for anyone about to add a feature, a domain or access control. Rules for writing code are in `CLAUDE.md`;
what a model is, is in [models](models.md).

## Layout

```
src/Linkbase/
  Program.cs            Services, middleware, health probes, the /model/{key} switch, command-line commands
  Components/           App shell and layout (MainLayout, theme, Quick-find, hotkeys help) and the guide's frame (Guide/)
  Core/                 Generic. Knows no domain
    Models/             ModelCatalog (configured and app-made models), ModelCreator and NewModelPage (a new model with its own database), CurrentModel (this circuit's model), ModelFile, ModelLoader
    Graph/              GraphRepository and GraphPattern: the graph_nodes / graph_edges views and pattern search
    Explore/            The Explore page, pattern search bar, Ask box, table, export, label styles
    Ask/                Plain-English questions: the compiler, PatternChecker (checks the answer), QuestionService
    Records/            Generated list, record and edit pages, RecordRepository (reads, writes, merge, hierarchy), RecordHooks, ExternalRefRepository
    History/            ChangeRecorder (writes the audit trail), HistoryRepository (reads it), ChangeReverter
    Views/              Saved views and the state a page keeps in its address (ViewState)
    Paths/, Timeline/   Find a path, Trace (TraceRepository), and the timeline page
    Cleanup/, Home/     The cleanup page, and what the home page reads
    Import/             The Import page: one tab for each way in
    CsvImport/          CSV upload and column mapping, and what New model starts from
    Extraction/         Reading text with AI: the model call, the validator, matching to existing records
    Staging/            Stage, review, commit and undo for any import (ImportStagingService)
    IDomainModule.cs    What a domain plugs in
  Data/                 The one LinkbaseDbContext, entities, enums, the single InitialCreate migration
  Domains/Climbing/     Everything climbing-specific
    ClimbingDomain.cs   The IDomainModule: nav items, label styles, services, command-line commands
    Data/               ClimbingRecordHook, GradeRanking, trips (TripBuilder, GenericTripService), LogbookRepository,
                        InsightsRepository
    Import/             UKC and Mountain Project parsers, corrections, GenericAscentImportService and its commands
    Features/           Pages: Logbook, Insights, and the climbing guide
  wwwroot/js/           graph.js (draws the graph and reports clicks), hotkeys.js, download.js
models/                 Model files (JSON plus CSV) for the climbing, demo, consultancy and Swiss rail models
tests/Linkbase.Tests/   One project: unit, Postgres (Testcontainers), bUnit page and Playwright browser tests
```

## How a request reaches data

1. A page or component injects a repository or service. Components never touch a `DbContext`.
2. The repository asks `IDbContextFactory<LinkbaseDbContext>` for a short-lived context. In the app that factory is
   `ModelDbContextFactory`, which opens the database of the model the circuit is on (`CurrentModel`).
3. Reads project to DTO records; writes go through `RecordRepository` or a service, inside one transaction.
4. Writes through `RecordRepository` run the model's domain hooks (`RecordHooks`) and keep `link_definition.links` in step.

The one exception is a scoped `LinkbaseDbContext` registered in `Program.cs` for Data Protection's key store only.

## Extension points

| To add | Do this |
| --- | --- |
| A new model with no code | A model file under `models/`, loaded with `model load` ([models](models.md)) |
| Behaviour for some types | An `IRecordHook` in a domain: it sets fields people can't edit, and derives records and links when others change |
| A domain | A folder under `Domains/`, an `IDomainModule`, and an entry in `RegisteredDomains`; models opt in with `Domains` in their configuration |
| Pages for a domain | `Domains/<Domain>/Features/<Feature>/`, listed in the module's `NavItems` |
| A new importer | Work out records and links, then stage them with `ImportStagingService` so review, commit and undo come free |
| A new table or column | Change the entities and add a migration; add a view to the migration's SQL if the graph needs it |

## Where access control would go

Nothing here is implemented (see [security-roadmap](history/security-roadmap.md) and [security-design](history/security-design.md)). The places a policy has to reach:

- **Who is asking.** Every owned row has a `user_id`, and the app assumes one user (`AppUser.DefaultId`). Sign-in replaces that
  assumption in the repositories' write paths and in the context factory.
- **Reads.** Every read is a repository method returning a DTO. Record values come from `node.props` JSON, so a field policy
  has to filter in `RecordRepository` (`Reads`, `Values`), `GraphRepository`, and the climbing repositories, which read
  props directly. Moving a sensitive field out of JSON into a typed column is the other route.
- **Writes.** `RecordRepository.Writes`, `ImportStagingService` and the loader. All write in one
  transaction, so a check at the start covers the batch.
- **Model choice.** `ModelCatalog` decides which models exist and `/model/{key}` switches; per-customer instances are
  configured by listing only their models.
- **Search and export.** Explore's search text (`node.search_text`) holds every property value, and the export methods
  in `ExploreExport` write what is on the canvas, so both need to respect a field policy too.
