# Where things live

A map of the code for anyone about to add a feature, a domain or a security rule. Rules for writing code are in `CLAUDE.md`;
what a model is, is in [models](models.md).

## Layout

```
src/Linkbase/
  Program.cs            The pipeline, health probes and command-line commands
  Startup.cs            The services (AddLinkbaseSecurity, AddLinkbaseCore), the security headers and the /model/{key} switch
  Components/           App shell and layout (MainLayout, theme, Quick-find, hotkeys help) and the guide's frame (Guide/)
  Core/                 Generic. Knows no domain
    Models/             ModelCatalog (configured and app-made models), ModelCreator and NewModelPage (a new model with its own database), CurrentModel (this circuit's model), ModelFile, ModelLoader
    Graph/              GraphRepository (behind IGraphRepository) and GraphPattern: the graph_nodes / graph_edges views and pattern search
    Explore/            The Explore page (CanvasGraph holds the nodes on the canvas), pattern search bar, Ask box, table, export, label styles
    Ask/                Plain-English questions: the compiler, PatternChecker (checks the answer), QuestionService
    Records/            Generated list, record and edit pages, RecordRepository (behind IRecordRepository: writes, merge, hierarchy, with the reads in RecordReader), RecordHooks, ExternalRefRepository
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

The one exception is a scoped `LinkbaseDbContext` registered in `Startup.cs` for Data Protection's key store only.

## Extension points

| To add | Do this |
| --- | --- |
| A new model with no code | A model file under `models/`, loaded with `model load` ([models](models.md)) |
| Behaviour for some types | An `IRecordHook` in a domain: it sets fields people can't edit, and derives records and links when others change |
| A domain | A folder under `Domains/`, an `IDomainModule`, and an entry in `RegisteredDomains`; models opt in with `Domains` in their configuration |
| Pages for a domain | `Domains/<Domain>/Features/<Feature>/`, listed in the module's `NavItems` |
| A new importer | Work out records and links, then stage them with `ImportStagingService` so review, commit and undo come free |
| A new table or column | Change the entities and add a migration; add a view to the migration's SQL if the graph needs it |

## Where access control lives

Built, and off unless a model turns it on (`model security on`). The design is in [security-design](history/security-design.md);
what a model file says is in [models](models.md#security). Where each piece is:

- **Who is asking.** `ICurrentUserAccessor` (`Core/Security/CurrentUser.cs`) gives the user, from `Security:Identity`:
  `configured`, `principal` (whoever ASP.NET Core authentication signed in) or `demo`. `DirectoryViewerResolver` turns that
  user into a `Viewer` using the grant sources the model names (its `users` table, a directory API, claims or a custom
  provider). With no identity a viewer holds nothing and sees nothing of a model that uses levels.
- **Rows.** Postgres row-level security does it (`RowSecurity.cs`). `ModelDbContextFactory` gives every context a
  `ViewerConnectionInterceptor`, which tells the database the viewer each time a connection opens, so raw SQL, the graph views
  and free-text search are covered as well as LINQ. A model with security off pays nothing.
- **Values.** Row security can't mask one field of a visible record, so `FieldPolicy` does it in code: a hidden value shows
  as a mask and can't be filtered, sorted or searched on. The readers that do their own reads (`RecordRepository`,
  `GraphRepository`, history, Insights) ask it. `PropertyLabelSql` covers the per-record locks in SQL.
- **Labels.** `LabelRepository` changes them, in bulk or one at a time, as one history change set. `LoaderLabels` reads them
  from the model file and CSVs.
- **One rule.** `VisibilityRule` states it in C#; the policies and the field policy are tested against it.
- **Writes.** Row policies apply to them too, so a viewer can't change what they can't see. Nobody is checked yet for
  *who may change labels* (SEC-8). Writes still go through `RecordRepository.Writes`, `ImportStagingService` and the loader,
  each in one transaction.
- **Not built.** A production sign-in scheme. A deployment adds its own and uses `principal`. Until then run the app only where
  you trust everyone who can reach it.
- **Model choice.** `ModelCatalog` decides which models exist and `/model/{key}` switches; per-customer instances are
  configured by listing only their models.
