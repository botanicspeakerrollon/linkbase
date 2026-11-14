# Security design: labels and row-level security

Nothing here is implemented. This is the design for the visibility rules that [security-roadmap](security-roadmap.md)
left open. It replaces that roadmap's step 4 ("consider row-level security later"): the rules below are enforced by the
database for rows, and by one field policy for property values.

The example used throughout is a bank, but nothing in the design is specific to one. The bank's words (classification,
department, project) are only a model's configuration: the design has three **kinds** of part, and a model names them,
fills in their values, and chooses which it uses. `models/consultancy` has a worked example.

## Principles

1. **Off means invisible.** A model that doesn't turn security on behaves exactly as it does today: no sign-in prompt, no
   labels in the UI, no extra queries, no measurable slowdown.
2. **Fail closed.** With security on, a query that doesn't say who is asking returns nothing.
3. **One rule, evaluated in one place per layer.** The rule is defined once (below), implemented once in SQL for
   rows and once in C# for values, and both are tested against the same oracle.
4. **Hidden things don't leave traces.** No counts, search hits, graph edges, history entries or error messages may reveal
   that something the viewer can't see exists. A hidden *property* on a visible record shows as a mask; a hidden *record*
   isn't there at all.

## The label

Every node, link and property can carry a label of up to three parts, one of each kind.

| Kind (key in the model file) | The item holds | The user holds | Passes when | The bank's name for it |
| --- | --- | --- | --- | --- |
| Level (`level`) | one value from the part's ordered list | one value | the user's value is at least the item's | Classification: `public` < `private` |
| Any of (`anyOf`) | a set | a set | the sets overlap: **any one** | Department: Finance, HR… |
| All of (`allOf`) | a set | a set | the item's set is a subset of the user's: **every one** | Project: ProjX, ProjY… |

A model **opts in** to each kind by listing it in its `security` section, and gives it a name (`"name": "Department"`),
which is all the app shows people. A kind a model doesn't list doesn't exist for that model: no column is checked, no
picker is shown and the rule skips it. A model that lists none has security off. The item is visible only if **every part
the model uses** passes.

Each model has at most one part of each kind. If a model ever needs two independent sets of one kind, that is a new design.

- An item with **no departments** is unrestricted on that part (stored as a `'*'` sentinel; see Postgres mechanics). An item with **no projects** passes the all-of test
  trivially. The default label (`public`, no departments, no projects) is visible to everyone.
- The level list, and optionally the allowed values of the other two parts, are defined per model as arbitrary names.
  There is no second classification axis; a model that wants "personal" or "commercial" makes them levels or members of a set.
- Values are matched exactly, including case. They may not contain `{`, `}`, `,`, `"`, `\` or control characters, because they
  travel in Postgres array literals, and `*` is reserved.
- Example: item A is `public`, departments {Finance, HR}, projects {ProjX, ProjY}. A user sees it if they hold public
  clearance, **and** Finance or HR, **and** both ProjX and ProjY.

### Which label applies

- **Nodes and links** each carry their own label. A link is visible only if its own label passes **and both end nodes are
  visible**, otherwise the link would reveal that a hidden node exists.
- **Properties** are labelled in two places: a default on the property definition in the model (for example, `notes` is
  always `private`), and an optional override on a single record's property. A property is shown only if the record's label,
  the definition's label **and** any override all pass. They are evaluated separately, not merged, because two "any of"
  department lists can't be combined into one set without changing the meaning.
- **Defaults** come from the model, then the node type, then the property definition. A record created with no label takes
  its node type's default.

## Enforcement

The design is a hybrid, because the two things being protected live in different shapes.

### Rows: Postgres row-level security

Rows (nodes, links, and everything that points at them) are protected by Postgres policies, so raw SQL and the graph views
can't bypass them and a missing `Where` in a repository can't leak.

- **Typed columns.** `node` and `node_link` gain `label_level smallint not null default 0`,
  `label_any text[] not null default '{*}'` and `label_all text[] not null default '{}'`. They are constants with defaults,
  so adding them is instant and costs nothing in a model that never uses them. A level's value maps to its rank (its position in the
  part's list); a part the model doesn't use keeps its default and is never checked. This also keeps to the rule that sensitive data lives in typed columns.
- **Two database roles.** The owner role runs migrations, backups and restores, and bypasses the policies. The application
  connects as a separate role with no `BYPASSRLS`, and the tables use `FORCE ROW LEVEL SECURITY` so the owner is bound too
  when the app happens to use it. Superusers always bypass, so the app must never connect as one.
- **Who is asking.** A connection interceptor on the one `IDbContextFactory` sets three session variables
  (`linkbase.level`, `linkbase.any`, `linkbase.all`) when a connection is opened, and Npgsql's pool reset clears
  them when it's returned. If the variables are unset, the policy returns no rows. Every context comes from the factory
  (an existing rule), so every query is covered, including raw SQL.
- **The policy** is one SQL function used by every policy, written so the planner evaluates the session settings once per
  statement and can use GIN indexes on the arrays (`label_any && …`, `label_all <@ …`).
- **Links** need the endpoints' visibility. The policy checks the two end nodes by primary key. This is the part to
  measure first on the large sample.
- **Graph views** (`graph_nodes`, `graph_edges`, the `climbing_*` views) become `security_invoker` views (Postgres 15+; the
  repo is on 16), so the policies apply as the caller instead of as the view's owner.
- **Side tables** inherit from the row they describe, by policy: `external_ref` (visible only if its node is), `saved_view`
  contents (hidden nodes drop out), history entries (see below), and staged import rows.
- **Writes.** Policies use `WITH CHECK`, so a user can only create or edit rows they could see, and can't write a row
  they couldn't read back.
- **Turning it on.** `ALTER TABLE … ENABLE ROW LEVEL SECURITY` is run per model database when the model's `security`
  setting is switched on, and reversed when it's switched off. Each model has its own database, so one model's choice never
  touches another.

### Properties: the field policy

Property values live in `node.props` JSON, which a row policy can't look inside, so values are masked in the application.

- One `FieldPolicy` service takes the viewer and a record's labels and decides, per property, show or mask. Every DTO
  projection and every graph query that returns values goes through it. A masked value is returned as a `Masked` marker, not
  an empty string, so the UI can draw `••••`, make the field read-only, and know the value exists.
- **Per-record overrides** live in a sparse table, `node_property_label (node_id, key, label…)`, with its own policy. A
  model that never overrides has an empty table.
- **Filtering and sorting** on a masked property must not leak. Rows whose value is masked never match a filter on it, and
  never contribute to a sort order.
- **Search.** `node.search_text` holds every value today, which would leak masked ones. Properties that carry a label
  (in the definition or an override) stay out of `search_text`, and a restricted-property search runs a slower path that
  applies the field policy per row. Unlabelled models keep today's index unchanged.
- **Export and the browser.** The CSV/Explore exports and the data sent to `graph.js` are built from the masked DTOs, never
  from raw props.

### Other places that repeat data

| Place | Rule |
| --- | --- |
| Audit history (`ChangeRecorder`) | Each entry stores a snapshot of the record's label at the time. It is visible only if the viewer passes that snapshot **and** the record's current label. A masked property's old and new values are masked. Relabelling is itself an entry. |
| `import_batch.raw_payload` | Owner/administrator only when security is on. |
| `link_definition.links` | A global count reveals hidden links. With security on, counts are computed for the viewer, or shown only to administrators. To decide when we measure. |
| Totals, "N results", pagination | Computed after the policy, never from a cached unfiltered count. |
| Error messages | A foreign-key failure that names a hidden record must report a generic "in use by something you can't see". |
| Backups | Contain everything. Treat them as the most sensitive thing in the system and encrypt them (`docs/operations.md`). |

## Postgres mechanics

This is the hardest part of the feature, so here is how the pieces work in Postgres 16, with the manual pages to read.
Links are to the version 16 manual; the SQL is a sketch of the shape, not final code.

### How row-level security works

With `ENABLE ROW LEVEL SECURITY` on a table, every query by a role that is subject to policies sees only rows for which a
policy's `USING` expression is true; `WITH CHECK` applies the same idea to rows being inserted or updated. If the table has
RLS enabled and **no** policy applies, the role sees nothing (default deny). The planner adds the policy expression to the
query's `WHERE` clause, so it works with joins, subqueries and raw SQL alike.

Who is exempt, which matters for our two roles:

- Superusers and roles with `BYPASSRLS` always bypass.
- The table's **owner** bypasses unless the table also has `FORCE ROW LEVEL SECURITY`.
- Referential-integrity checks (foreign keys) and unique-constraint checks **ignore** policies. A user inserting a key that
  already exists in a hidden row gets a duplicate-key error, which reveals that the row exists. This is why the design
  turns errors about hidden rows into a generic message.
- `pg_dump` fails under RLS unless the role can bypass it (or `row_security` is off), so backups use the owner role
  (see [operations](operations.md)).

Reading: [Row Security Policies](https://www.postgresql.org/docs/16/ddl-rowsecurity.html),
[CREATE POLICY](https://www.postgresql.org/docs/16/sql-createpolicy.html),
[ALTER TABLE](https://www.postgresql.org/docs/16/sql-altertable.html) (`ENABLE` / `FORCE ROW LEVEL SECURITY`),
[CREATE ROLE](https://www.postgresql.org/docs/16/sql-createrole.html) (`BYPASSRLS`).

### Telling the database who is asking

A policy can't see the .NET user, so the app passes the viewer as **custom settings**, namespaced `linkbase.`, readable in
SQL with `current_setting('linkbase.level', true)`. The second argument makes an unset setting return null instead of an
error, and the helper turns null into a level that matches nothing, which is what makes the design fail closed.

```sql
create schema linkbase_sec;

-- Unset => -1, which no row's classification is <= : nothing is visible.
create function linkbase_sec.viewer_level() returns int
  language sql stable as
  $$ select coalesce(nullif(current_setting('linkbase.level', true), '')::int, -1) $$;

create function linkbase_sec.viewer_any() returns text[]
  language sql stable as
  $$ select coalesce(nullif(current_setting('linkbase.any', true), '')::text[], '{}') $$;

create function linkbase_sec.viewer_all() returns text[]
  language sql stable as
  $$ select coalesce(nullif(current_setting('linkbase.all', true), '')::text[], '{}') $$;
```

The app sets them with `select set_config('linkbase.level', '1', false)` and so on, from an EF Core `DbConnectionInterceptor`
(`ConnectionOpened`), using `IViewerContext`. `false` makes the setting last for the session; `true` (the same as
`SET LOCAL`) would last only for the current transaction, which is no use for queries run outside one.

Connection pooling is the danger: a pooled connection that kept the last viewer's settings would be a leak. Npgsql resets
the session when it returns a connection to the pool (`DISCARD ALL`, which includes `RESET ALL` and so clears custom
settings), unless `No Reset On Close` is set, which we must never do. The interceptor also sets the values on every open,
and a test checks that a connection never starts with a stale viewer. For this to be per user, the
`IDbContextFactory<LinkbaseDbContext>` must be registered with a **scoped** lifetime so each Blazor circuit gets a factory
that knows its own viewer.

Reading: [Customized Options](https://www.postgresql.org/docs/16/runtime-config-custom.html),
[System Administration Functions](https://www.postgresql.org/docs/16/functions-admin.html) (`set_config`, `current_setting`),
[SET](https://www.postgresql.org/docs/16/sql-set.html), [DISCARD](https://www.postgresql.org/docs/16/sql-discard.html),
[Npgsql connection pooling](https://www.npgsql.org/doc/connection-string-parameters.html#pooling).

### The policy

```sql
create function linkbase_sec.can_see(cls int, depts text[], projs text[]) returns boolean
  language sql stable as $$
    select cls <= (select linkbase_sec.viewer_level())
       and depts && (select linkbase_sec.viewer_any())   -- any of
       and projs <@ (select linkbase_sec.viewer_all())      -- all of
  $$;

alter table node enable row level security;
alter table node force row level security;

create policy node_label on node
  for all to linkbase_app
  using      (linkbase_sec.can_see(label_level, label_any, label_all))
  with check (linkbase_sec.can_see(label_level, label_any, label_all));
```

Points to know:

- `&&` (overlap) and `<@` (is contained by) are the array operators that express "any of" and "all of". `x <@ y` is true
  when every element of `x` is in `y`, and an empty `x` is contained by anything, so an item with no projects passes.
- `&&` with an empty `label_any` is false, which is the wrong answer for "no departments means unrestricted". The design
  avoids a special case: an unrestricted item stores the sentinel `{'*'}` (the column's default), and every viewer's any-of set always includes
  `'*'`. The model layer hides this; the database sees only plain overlap, which a GIN index can serve. The alternative,
  `cardinality(label_any) = 0 or …`, defeats the index.
- `(select …)` around each helper makes the planner evaluate it **once per statement** (an InitPlan) instead of once per row.
  `stable` is the right volatility: constant within a statement, not constant forever. Check with `EXPLAIN` that the helpers
  appear as `InitPlan` and not under the scan.
- Indexes for the array tests are GIN indexes (`create index … using gin (label_any)`); the built-in `array_ops` operator
  class supports `&&`, `<@`, `@>` and `=`. A GIN index is only worth having when the policy is selective, so create them only
  when security is turned on, never when it is off.
- **Leakproof.** To stop a user's own filter from leaking hidden rows (for example an expression that raises an error on a
  hidden row), Postgres evaluates policy conditions first, and lets a user condition run earlier only if its functions are
  marked `LEAKPROOF` (plain comparisons on built-in types are). Our helpers don't need to be leakproof; user-supplied
  functions in repository queries don't need it either, but the performance work must check that an unmarked function in a
  filter doesn't stop an index being used.

Reading: [Array Functions and Operators](https://www.postgresql.org/docs/16/functions-array.html),
[GIN Indexes](https://www.postgresql.org/docs/16/gin.html),
[Function Volatility Categories](https://www.postgresql.org/docs/16/xfunc-volatility.html),
[CREATE FUNCTION](https://www.postgresql.org/docs/16/sql-createfunction.html) (`LEAKPROOF`),
[Rules and Privileges](https://www.postgresql.org/docs/16/rules-privileges.html) (how view and policy conditions are ordered).

### Links, views and the dependent tables

- **Links.** `node_link`'s policy is its own `can_see(...)` **and** an `exists` on each end node by primary key. Because
  `node` has a policy too, those subqueries are filtered automatically; the design depends on that, and a test pins it.
- **Views.** A view normally runs with its owner's rights, so a view owned by the owner role would **bypass** the policies.
  `create view graph_nodes with (security_invoker = true) as …` (or `alter view … set (security_invoker = true)`) makes the
  view use the caller's rights, so the policies apply. Every view over labelled tables needs this, including the
  `climbing_*` views, and the coverage test checks `pg_class.reloptions` for it.
- **Dependent tables.** `external_ref` and the other side tables get a policy of the form
  `exists (select 1 from node n where n.id = node_id)`; the exists is itself filtered by `node`'s policy.
- **Toggling.** Turning security on creates the policies, the GIN indexes and the helper functions, then enables RLS, in
  one migration-style script (so a failed step can't leave RLS enabled with no policy, which would hide everything). Turning
  it off drops them in the reverse order. `pg_policies` and `pg_class.relrowsecurity` show the current state.

Reading: [CREATE VIEW](https://www.postgresql.org/docs/16/sql-createview.html) (`security_invoker`),
[pg_policies](https://www.postgresql.org/docs/16/view-pg-policies.html),
[pg_class](https://www.postgresql.org/docs/16/catalog-pg-class.html) (`relrowsecurity`, `relforcerowsecurity`, `reloptions`).

### Measuring it

Use `EXPLAIN (ANALYZE, BUFFERS)` on the `swiss_rail` sample with security on and off, and with several viewers (all-seeing,
narrow, none). Look for: helpers as `InitPlan`, the GIN index in a `Bitmap Index Scan`, and `graph_nodes … order by … limit`
still reaching its rows without walking the graph. See [performance](performance.md).
Reading: [EXPLAIN](https://www.postgresql.org/docs/16/sql-explain.html), [Using EXPLAIN](https://www.postgresql.org/docs/16/using-explain.html).

### What this does not protect against

- **The app role can set its own session variables.** Anything that can run arbitrary SQL as the app role could set
  `linkbase.level` itself. Raw SQL stays parameterised (`FromSql`, never string concatenation), and the app role has no
  rights beyond the tables it needs. The stricter alternative, one database role per user with `SET ROLE`, was rejected: it
  doesn't fit pooling or a large user list. It is worth revisiting if the threat model includes SQL injection.
- **The database owner and anyone with `BYPASSRLS` or superuser rights** see everything, including backups.
- **Timing and plan side channels** are not addressed.
- Masked property values are protected by application code, not by the database. That is why the field policy has its own
  oracle and coverage tests.

## Changing labels

- Only a viewer who can see an item can edit or relabel it.
- Changing labels needs a `label-admin` permission. A user can only assign a label they themselves pass, so no one can
  create or relabel something beyond their own clearance. Lowering a classification, or removing a department or project,
  needs a reason and is recorded through `ChangeRecorder` like any other edit.
- A relabel is an edit, so it sets `updated_at`. Import undo therefore keeps a record that has been relabelled since import.

### Bulk assignment

Mass labelling is a first-class feature, not an afterthought.

- **By selection**: records chosen in Explore or a list.
- **By filter**: every record matching a filter or saved view, with a count and a preview before it runs.
- **By import**: a label column in the CSV, or a default label for a batch.
- **By default**: set the label for a node type or property definition, optionally applying it to existing records.
- Each runs as one transaction in chunks, through `ChangeRecorder` with a single reason, and reports how many records
  changed. Bulk jobs run as the acting user, so the policy also decides what they can touch.

## Where grants come from

A viewer's grants are a level and two sets. The app doesn't own them; a company brings its own directory. One company owns a
deployment, so grants are the same for every model in it. Each **part** chooses its own source in the model file, so the level
may be hard-coded while departments come from an API.

| Source | What it is |
| --- | --- |
| `config` (default) | A `users` table in the model file: `{ "alice": { "level": "private", "anyOf": ["Finance"], "allOf": ["ProjX"] } }`. For demos, tests and small teams. |
| `api` | An HTTP `GET` to a URL the app doesn't own, with `{user}` replaced by the user's id. The response is JSON; `path` names the property holding the value (a string for a level, a string or an array of strings for a set). An optional bearer token is read from the named app setting, never from the model file. |

- **Caching.** `cacheSeconds` is any number of seconds, including 0 for none. Parts that share a URL and a cache time share one
  request and one cache entry. The default is 300.
- **Failing closed.** If the API is down, slow or returns something unexpected, and nothing fresh is cached, the part grants
  **nothing**: the viewer sees less, never more. Expired entries are never served, so a revoked grant stops working
  within `cacheSeconds` at most. Failures are logged and not cached.
- An unknown level name, or a value with a forbidden character, is dropped.
- The grants are resolved into a `Viewer` once per request or circuit; policies and the field policy only ever see the
  `Viewer`.

## Users and sign-in

Labels are meaningless without identities, so ASP.NET Core Identity (roadmap step 1) is still required. The design doesn't
wait for it:

- Everything is written against an `IViewerContext` (level, departments, projects, roles). The first implementation is a
  fixed or test-supplied viewer, so labels, policies and the field policy can be built and tested before sign-in exists.
- Users' grants come from the sources above. The user's id (from Identity, later) is the only thing the app has to supply.

## Model and UI

- The model file gains a `security` section: the parts it uses (`level`, `anyOf`, `allOf`), each with a `name`, the level's
  ordered `values`, and where grants come from. The model editor gets a matching tab. See [models](models.md#security).
- With security off, none of it appears. With it on, records show a label chip, the editor has a label picker, and masked
  values show as `••••`. Everything stays drivable from the keyboard.

## Performance

- Off: the only difference is three defaulted columns. No policy, no interceptor work, no changed query plans.
- On: measure on the `swiss_rail` sample, with realistic labels, against the current numbers in
  [performance](performance.md) before choosing between policy shapes. The budget is set after the baseline is measured, and
  `graph_nodes … order by … limit` must still find its rows first.

## Testing

- **Oracle.** A pure C# function implements the rule. Property-based tests build random labelled graphs and random viewers
  and require every repository read, the graph queries, search, exports and history to return exactly what the oracle allows.
- **Coverage test.** In the style of `ChangeRecorderCoverageTests`: fail if a table that holds record-derived data has no
  policy, or a repository method returns values without going through `FieldPolicy`.
- **Role test.** The application role can't bypass the policies, and an unset viewer sees nothing.
- **Leak tests** for the side channels above: counts, search, edges, history, errors, exports.
- Database tests use Testcontainers as usual.

## Build order

1. `IViewerContext`, the model's `security` section, the grant sources, and the rule as a pure, tested function (the
   oracle). **Built.**
2. Label columns, the model `security` setting, labels from CSV and bulk label edits. **Built.** Not yet: merging records
   (Cleanup) doesn't carry labels, and nobody is checked for permission to label.
3. Row policies, the roles, the connection interceptor, `security_invoker` views, and the performance run.
4. `FieldPolicy`, the property label tables, masking in DTOs, and search.
5. History, `external_ref`, saved views and the other side tables.
6. Label UI, the model editor tab and bulk assignment.
7. Identity and the user directory.

## Open questions

- Should the level part be strictly ordered, or can a model have unordered levels? This design assumes ordered, with a
  one-value list as the degenerate case.
- Is a `label-admin` permission enough, or does labelling need a separate approver?
- What do administrators see in counts and history, and is there an audited "break glass" access?
- Does `CLAUDE.md`'s "sensitive fields in typed columns" rule stand? Row labels are typed; property labels are not, since values
  stay in `props`. This design says the rule is met by masking in the field policy; confirm before changing the rule.
