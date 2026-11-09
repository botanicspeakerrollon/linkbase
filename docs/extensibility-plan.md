# Product plan

The app is now called Linkbase (the name is unchecked for trademarks). The aim is to turn it into a hosted product for small companies that want to see how their customers, suppliers,
projects and people connect, without buying a graph database or employing someone to model the data. The pitch isn't a
cheaper Neo4j. Neo4j has a free edition, and what these companies lack is the skill to use it.

The table below says how far each phase has got. Market claims here are unresearched, and any customer's data needs UK GDPR advice first.

## What it builds on

The graph layer was already close to model-agnostic: Explore only sees rows shaped like
`node_id, label, name, props` and `source, rel_type, target`. The strengths to keep are the search bar that needs no query
language, the Explore canvas, and AI extraction with human review before anything is saved.

## Decisions

- **Hybrid data model.** Climbing stays as typed tables. A generic store is added beside it for everything else, and a model
  becomes a typed module only when it earns one. Moving climbing into the generic store would lose grade ranking and
  derived trips, and mean redoing a migration that was verified row by row. **Under review:**
  [history/climbing-to-generic-plan.md](history/climbing-to-generic-plan.md) argues those reasons no longer hold, and sets out the move.
- **One app instance and one database per customer.** Sharing a database means one missed filter could expose a customer's
  data to another. Separate databases rule that out, and per-database backup and restore already exist.
- **The generic store is the product.** Climbing becomes the demo and a personal instance, and keeps working throughout.
- **One permission check on every read.** With several users, field visibility has to be enforced in one place, including
  Explore's raw SQL and free-text search, or that search could reveal hidden fields. `CLAUDE.md` says sensitive fields are
  typed columns, which a generic store breaks: update it and `docs/security-roadmap.md` when this is designed.

## Phases

| | Phase | Size | State |
| --- | --- | --- | --- |
| 0 | Find a pilot company and one of its real datasets | No code | **Done** |
| 1 | Generic Explore | Small | **Done** |
| 2 | Performance on a large dataset | Small | **First pass done.** See [performance](performance.md) |
| 3 | Foundations: private remote, CI, Dockerfile, bundled graph libraries, health checks, logging | Small | **Done** |
| 4 | Separate `Core/` from `Domains/Climbing/`, and remove the single-user assumption | Medium | **Split done.** Single-user assumption still to go |
| 5 | Generic model store, model files, switching between models, generated pages | Large | **Done**, apart from values on links |
| 6 | Sign-in, roles and field permissions, finished before a pilot uses real data | Large | Not started |
| 7 | Import: a CSV mapping wizard, then AI extraction driven by the model | Large | **CSV wizard done.** AI extraction still to go |
| 8 | Hosting: per-customer setup, scheduled off-site backups, monitoring, audit log | Medium | Not started |
| 9 | AI model setup, and plain-English questions that compile to checked search patterns, never raw SQL | Medium | Not started |

### Phase 1: done

Explore's types and relationships come from the data, with the most common types first. Unknown types get a stable colour
and a default icon. Captions and links belong to a per-label `LabelStyle`. The date and discipline filters became generic
property filters. A browser test browses a station-and-line model the app has never seen, in its own database.

### Phase 4

Done: graph and Explore moved to `Core/`, and climbing to `Domains/Climbing/`, plugged in through `IDomainModule`, which supplies
styles, navigation, services, commands and search rules (it supplied a home panel too, until every model's home became the
same list of types). There's still one `DbContext` and one migration
history. The tests passed with changes only to namespaces and `using` lines.

Still to do: every query assumes `AppUser.DefaultId`, and `Data/` still holds the climbing entities. The climbing `Route`
type also needs a global alias, because ASP.NET Core has one too.

### Phase 5

Done: `node_type`, `field_definition` and `link_definition` describe a model, and `node` and `node_link` hold its data, with
real foreign keys, a stored `search_text` and trigram indexes. A JSON model file names the CSV columns everything comes from,
and `model load` replaces a model in one transaction. Each model has its own database, listed under `Models` and switched in
the rail. The Swiss sample is now loaded this way. See [models](models.md).

Done since: generated list and record pages for each type, with adding, editing, deleting and linking checked against the
definitions, so a model's data can change without reloading. Still to do: values on links, and the pilot.

Done when the pilot's model works end to end without writing new pages.

### Phase 7

Done: a CSV wizard (`Core/CsvImport`) imports records of a new or existing type, or links between them, checks every value
against its field's type first, and undoes an import. See [models](models.md#importing-a-csv-file). Existing records are
skipped, not updated, which keeps undo simple; updating them is the obvious next request.

Still to do: build the extraction tool's schema from the model definition, and apply the "must appear in the source text" check to every
field. Add staging, review and undo as a new generic path, and leave the climbing import alone, because it's tightly shaped
around crags and routes. Customers must be told text goes to Anthropic, and be able to turn it off.