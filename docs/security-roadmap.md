# Security roadmap

Nothing here is implemented. The app is single-user with no sign-in, and should only be run locally
until the first step below is done.

The goal is field-level visibility later: for example, ascent notes and partners stay private while
routes and crags are shareable, or some crags' locations are hidden.

## What's already in place

- Climbing now lives in the generic store, so its values (including `notes`, partners and crag coordinates) are inside `node.props` JSON, not typed columns. Field-level security has to be applied in the repositories and views, or those fields moved into typed columns first.
- Every `node`, `import_batch` and `graph_scene` row has a `user_id`.
- All reads go through repositories that project to DTOs, so one place can apply a field policy.
- The graph views build `props` from named columns, so a new column can't leak into them by accident.

## Options

| Option | Covers | For | Against |
| --- | --- | --- | --- |
| ASP.NET Core Identity + policy-based authorization | Who the user is; which pages and actions they can use | Standard; works well with Blazor; needed by every other option | Adds tables and sign-in UI; says nothing about rows or fields by itself |
| EF Core global query filters | Which rows a user sees | Small change in one place; easy to test | Raw SQL, including the graph queries, bypasses it; `IgnoreQueryFilters` can skip it |
| Field policies in DTO projections | Which fields a user sees | Plain C#; easy to test; can show per-viewer values such as a rounded location | Only as complete as developers' discipline; graph SQL needs a matching policy |
| Postgres row-level security + column grants | Rows and columns, enforced by the database | Holds even for raw SQL and bugs in app code | Needs a database role or session variable per user, which is awkward with connection pooling; policies are harder to test and migrate; column grants fail on any query that selects a denied column, rather than masking it |

## Recommended path

1. Add Identity and policy-based authorization. Do this before exposing the app beyond localhost.
2. Add global query filters for row visibility, and apply the same condition in the graph queries.
3. Add one field policy service, used by the DTO projections and the graph queries.
4. Consider row-level security only if the app starts serving people other than its owner, as defense in
   depth for the rows and fields that matter most.

## Things to watch

- `graph_nodes` and `graph_edges` are views over raw columns. Once a policy exists, they need to accept
  the viewer or be filtered in the repository, or they become a way around it.
- `import_batch.raw_payload` holds the source data as JSON, which may include private fields. Treat it as
  owner-only. For text read by AI it holds only the extracted facts, not the page's text.
- Reading text sends pasted or fetched text to the Anthropic API. Your own notes and partners are never sent.
- `external_ref` has no `user_id`. It should inherit visibility from the row it points to.
