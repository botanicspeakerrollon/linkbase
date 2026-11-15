# Performance and the large sample

The climbing data is small: about 5,000 nodes and 10,000 links. To see how the stack copes at a realistic size for a
product, there's a large sample of real data, in its own Docker container, that the real app can run against.

## The large sample

It's the Swiss national public transport timetable from [gtfs.geops.ch](https://gtfs.geops.ch/), loaded as a generic model
from `models/swiss-rail/model.json`: 3,586,271 nodes and 45,564,262 relationships. That's 2,573,093 services, 943,759 lines,
68,942 stops, 477 agencies and 41,970,007 stop calls.

Switch to it in the rail as "Swiss public transport". It lives in the `swiss_rail` database of the `linkbase-samples` container,
on port 55433, beside the small `consultancy` model. The container starts with Docker, listens only on this PC, and has no
password, because it's public data. It's not in the backups: if it's lost, load it again.

## Loading it

```powershell
New-Item -ItemType Directory data/samples/gtfs-ch
curl.exe -L -o data/samples/gtfs-ch/gtfs_complete.zip https://gtfs.geops.ch/dl/gtfs_complete.zip
Expand-Archive data/samples/gtfs-ch/gtfs_complete.zip data/samples/gtfs-ch/extracted
docker run -d --name linkbase-samples --restart unless-stopped -e POSTGRES_DB=linkbase -e POSTGRES_USER=linkbase -e POSTGRES_HOST_AUTH_METHOD=trust -p 127.0.0.1:55433:5432 postgres:16-alpine
docker exec linkbase-samples psql -U linkbase -d postgres -c "CREATE DATABASE swiss_rail"
dotnet ef database update --project src/Linkbase --connection "Host=127.0.0.1;Port=55433;Database=swiss_rail;Username=linkbase"
dotnet run --project src/Linkbase --launch-profile http -- model load models/swiss-rail/model.json swiss-rail
```

The load takes about 100 minutes, most of it on the 42M stop calls, and the database comes to 9 GB. The source files are 2.4 GB unzipped, in `data/samples/gtfs-ch` (git-ignored). The terms are those of
[opentransportdata.swiss](https://opentransportdata.swiss). geOps also asks that results don't misrepresent the raw data and
that published data is kept up to date, so check them before showing it to anyone.

## Measuring

```powershell
$env:LINKBASE_PERF_CONNECTION = "Host=127.0.0.1;Port=55433;Database=swiss_rail;Username=linkbase;Command Timeout=0"
$env:LINKBASE_PERF_REPORT = "perf.md"
dotnet test -c Release --filter "Category=Performance"
```

This runs the repository's real queries three times each. Without the variable, the test is skipped.

## Results

On 4 November 2026, on this PC with Postgres's default settings, one user at a time, on the `all` tier: 3.6M nodes and 45.6M
links. Medians of three runs. "Before" is before the search column below.

| What | Before | Now |
| --- | --- | --- |
| Open the page: schema | 1.8 s | 1.0 s |
| Graph model: node counts | 164 ms | 150 ms |
| Keystroke: suggestions for `Zür` | 5.1 s | **354 ms** |
| Keystroke: suggestions for just `Zü` | not measured | 1.7 s |
| Keystroke: values of a property | 103 ms | 21 ms |
| Search: first 100 of a type | 759 ms | 591 ms |
| Search: name contains, within a type | 413 ms | 35 ms |
| Search: free text across everything | 10.6 s | **262 ms** |
| Search: following two relationships | 1.3 s | 904 ms |
| Expand: a Zürich HB platform, 5,104 links, first 100 | 73 ms | 72 ms |
| Right-click menu: its counts | 860 ms | 74 ms |
| Draw: nodes and links for 100 nodes | 18 ms | 8 ms |

On the smaller `rail` tier, before the search column, free text took 1.7 s. Before sampling the schema query, opening the page
took 20.6 s.

Reading 5,000 rows of each type for the schema means that on a bigger type, a property or link that only appears on later
rows isn't offered.

## JIT is off

Postgres compiles a query to machine code (JIT) when the planner's cost estimate passes `jit_above_cost`, 100,000 by
default. The graph views look expensive to the planner even when the work is small, so the compiling took longer than the
query. The app turns JIT off for all its connections, in `ModelCatalog.ConnectionString`, which adds `Options=-c jit=off`.
The databases keep their default settings.

Measured on 7 November 2026, medians of five runs on the real climbing data. Every other query on the Ascents and
Insights pages, and the rest of Explore, came within 2 ms either way.

| Climbing | JIT on | JIT off |
| --- | --- | --- |
| Explore: schema | 710 ms | 60 ms |
| Insights: climbing days by month | 466 ms | 18 ms |
| Insights: ascents by month and country | 35 ms | 8 ms |
| Graph model: link counts (measured separately, before this change) | 1.5 s | 70 ms |

On the Swiss sample, medians of three runs from the performance test. The test ran twice in each order, and the results
agreed. The rows not shown changed by less than the difference between runs.

| Swiss sample | JIT on | JIT off |
| --- | --- | --- |
| Open the page: schema | 1.2 s | 565 ms |
| Search: following two relationships | 961 ms | 418 ms |
| Expand: a Zürich HB platform, first 100 | 75 ms | 33 ms |
| Right-click menu: its counts | 789 ms | **23 ms** |
| Keystroke: suggestions for `Zür` | 272 ms | 213 ms |

Nothing got slower. JIT pays off on long analytic queries over millions of rows, which this app doesn't run. Turn it back on
for one query with `SET LOCAL jit = on` inside a transaction if that changes.

## How search works

`graph_nodes` has a `search_text` column holding a node's name and property values. Free text and suggestions search that,
and near misses on names use pg_trgm's `%` operator, because unlike `similarity()` it can use an index.

- **Small tables, like climbing:** the view computes it live, so it's never stale, and it's quick enough without an index.
- **Large tables, like the sample:** it's a stored generated column with a trigram index, and the name has its own. On the
  `all` tier they took under 40 seconds to add.

The generic store should do what the sample does.

### Things to watch

- **Sorting with a limit.** Adding the index on `label` took expanding from 73 ms to over 2 minutes, because the planner chose to
  walk every node in label order, hoping to reach the limit early. The neighbour query now finds its rows first, in a
  materialized CTE, and sorts them afterwards. Any new query with `ORDER BY label ... LIMIT` over `graph_nodes` needs the same.
- **Two-letter searches** have too few characters for a trigram index, so they scan: 1.7 s for `Zü`. Each new keystroke cancels
  the last lookup, so it's only felt on a pause.
- **Re-measure after adding an index.** The performance test fails if any query takes over 2 minutes.

## Row security

Measured on a copy of `swiss_rail` with row security on (3.6 million nodes, 45 million links), comparing the owner, who
bypasses policies, with `linkbase_app` as a viewer who sees everything, so the policy filters nothing and only costs:

| Query | Owner | As `linkbase_app` |
| --- | --- | --- |
| 5,000 nodes of a type, with their links | 10 ms | 24 ms |
| `order by name limit 100` | 200 ms | 344 ms |
| Shortest path | 10 ms | 31 ms |

Three findings, each worth knowing before changing the policy:

- **Write the test inline in each policy.** Behind a SQL function, Postgres can't inline the sub-selects, so
  `current_setting` ran per row and everything was 12 times slower.
- **No GIN indexes on the label arrays.** With every row `{*}` they aren't selective, and the planner scanned all
  3.6 million entries as a bitmap, 3 to 30 times slower than no index.
- **Links cost the most**, because each needs its two end nodes checked by primary key. That is the 3 times above.

A narrow viewer (few rows visible) has not been measured on a large model; do that before relying on it there.

A viewer with hidden fields searches `graph_search_text(name, props - hidden)`, which the stored `search_text` column and its trigram
index can't serve, so free text for them scans. Everyone else, and every model without field labels, uses the index as before. It
hasn't been measured on the large sample.

With row security on, the relationship counts in Explore's model view are counted live through the policies
(`GraphRepository.ConnectionCountsAsync`) instead of read from `link_definition.links`, because the kept totals include links
the viewer can't see. That is a grouped count over every link, joined to both ends, so on tens of millions of links it will be
slow. Not measured on the large sample; if it hurts, cache it per viewer shape for a few minutes.

## Several users at once

`LoadTests` (`Category=Performance`) simulates people using one model together. Each simulated user loops over the queries
the Explore page runs, through the real `GraphRepository`, pausing about 200 ms between steps, and all of them share one
connection pool as the circuits of a real server would. What it searches for and expands is sampled from the model, so
it runs on any model. It reads only.

```powershell
pwsh -File scripts/load-test.ps1                      # small, medium and large samples, 1/5/10/25 users
pwsh -File scripts/load-test.ps1 -Users 1,10,50 -Seconds 30 -Think 0 -Models "mine=Host=...;Database=..."
```

The script sets `LINKBASE_LOAD_MODELS` (`name=connection` pairs separated by `|`), `LINKBASE_LOAD_USERS`, `LINKBASE_LOAD_SECONDS`
and `LINKBASE_LOAD_THINK_MS`, then writes a markdown report. Without `LINKBASE_LOAD_MODELS` the test is skipped. It never
writes, but point it at the samples container, not at the real logbook.

Measured on 15 November 2026 on this PC, which also ran the load generator and Docker, so the figures are indicative. Each level
ran 15 seconds after a warm-up, with JIT off.

| Model | Records | Users | Requests per second | Median | 95th | Slowest |
| --- | --- | --- | --- | --- | --- | --- |
| Small (consultancy) | 25 | 1 / 25 | 4.7 / 114.7 | 4 / 4 ms | 12 / 15 ms | 18 / 43 ms |
| Medium (demo climbing) | 2,267 | 1 / 25 | 4.6 / 114.5 | 5 / 5 ms | 11 / 16 ms | 24 / 60 ms |
| Large (Swiss public transport) | 3.6 M | 1 | 2.8 | 21 ms | 921 ms | 1.1 s |
| | | 5 | 14.0 | 18 ms | 606 ms | 1.3 s |
| | | 10 | 27.3 | 24 ms | 722 ms | 2.2 s |
| | | 25 | 38.4 | 54 ms | 2.4 s | 3.3 s |

- **Small and medium models don't notice 25 people.** Throughput grew in step with the users and latency didn't move, so the
  limit is people, not the database.
- **The large model's typical request is still fast, but the slow ones queue.** At 25 users the median stayed at 54 ms and the
  95th percentile went from 0.9 to 2.4 s. By step, the slow ones were opening the page (1.9 s median: the schema query samples
  5,000 rows of each type), the first 100 of a type, keystroke suggestions and free text. Expanding, drawing, the right-click
  menu and name-contains stayed under 100 ms at the 95th percentile.
- **Opening the page is the one to fix first for many users.** It costs about 1 s alone and 2 s at 25 users, and every new
  visitor pays it. The generic store reads the schema from the definition tables, which removes the sampling (see above).
- **The sample container fails under parallel queries.** At 25 users 4 of 605 requests failed with `could not resize shared
  memory segment ... No space left on device`: parallel query workers share the container's 64 MB `/dev/shm`. Recreate it with
  `--shm-size=1g`, or set `max_parallel_workers_per_gather = 0`, before reading much into a run at that level. A real server
  needs enough shared memory for its expected concurrency.
- **An unfiltered relationship search is the worst case.** Following a relationship from every record of a type took 11 s for
  one user on the large model, so the scenario starts from a named record, as a person would.

Not covered: row-security viewers (the test connects as the database owner, who bypasses the policies), writes alongside readers,
the real climbing logbook, the browser and SignalR circuits, and the memory a circuit costs.

### Limits: what more hardware fixes and what it doesn't

Measured on the large model only, because the small and medium ones were never the limit. The samples container had 14 CPUs
available (`docker update --cpus 14`; `--cpus 0` does *not* remove a limit) with PostgreSQL's default settings, and users
paused 1 ms between steps, so each number is capacity, not a realistic crowd. `LINKBASE_LOAD_ONLY=<text in a step's name>`
runs one step alone.

**One step at a time, 10 users, 10 s:**

| Step | Requests per second | Median | 95th |
| --- | --- | --- | --- |
| Open the page: schema and counts | 8.6 | 1169 ms | 1198 ms |
| Search: following a relationship from a named record | 21 | 457 ms | 496 ms |
| Keystroke: suggestions | 25 | 152 ms | 1335 ms |
| Search: first 100 of a type | 14 | 416 ms | 1982 ms |
| Search: free text, everything | 54 | 80 ms | 1138 ms |
| Search: name contains, within a type | 204 | 4 ms | 17 ms |
| Expand, right-click counts, draw, values of a property | 1,300 to 2,600 | 2 to 5 ms | under 11 ms |

Five steps cost a database core each for a second or so; the other four are close to free. A realistic mix is dominated by
the five, and opening the page is the worst per visit.

**What more resource fixes**

- **Database CPU.** Under load, the database is the bottleneck, and it is CPU-bound. Throughput rose roughly with the cores
  given to it (2, 4 and 14 CPUs gave about 8, 17 and 43 requests per second at 10 users). A bigger database tier or
  more cores moves the ceiling in proportion.
- **Shared memory.** The failures above are a container limit. `--shm-size`, or `max_parallel_workers_per_gather = 0`, removes
  them. On a managed server this is not a concern.
- **Not measured: `shared_buffers`.** The sample uses 160 MB for a 9.5 GB database. Changing it needs a restart of the samples
  container, which I did not do. Expect it to help the uncached scans (suggestions, first 100, free text) before anything else.
- **Per-session planner and memory settings changed nothing.** `work_mem`, parallel workers and the like, set through the
  connection's `Options`, made no measurable difference. Don't spend time there.

**What it doesn't fix**

- **Overload collapses, it doesn't degrade.** With all 14 CPUs and a pool of 20, throughput went 41.8, 34.5, 19.3 requests per
  second at 25, 50 and 100 saturated users, and the median went from 0.14 s to 2.5 s. Adding users past the database's
  capacity lowers throughput, because every query is a parallel plan fighting for the same cores. With the default pool of 100
  the same test reached 11.7 per second at 100 users, so a smaller pool helps a little (less contention) but only moves the
  collapse. The fix is to do less work per visit, not to queue it differently.
- **Nothing is cached.** Every visitor's page open reruns the schema sampling, every keystroke reruns suggestions and every
  circuit asks for the same counts. For a model that changes only on import, the schema, the node counts and the per-type
  counts could be cached per model and invalidated by a write, which would take opening the page (the costliest step) from
  about 1 s of database CPU per visit to none. This is the single largest gain available and it is code, not hardware.
- **One server process holds every circuit.** Blazor Server keeps each user's session in memory on one replica. More
  replicas need sticky sessions (the Helm chart sets ingress cookie affinity; the Azure template pins one replica). It scales
  out, but a replica that restarts drops its users' sessions, and per-circuit memory and CPU are **not measured** here (that
  needs the browser-level test). Don't plan replica counts from these numbers.
- **Connection arithmetic.** Each replica's pool defaults to 100 connections, and PostgreSQL defaults to 100 in total (a small
  Azure tier allows fewer). Two replicas at the default can exhaust the server. Size the pool times the replicas under
  `max_connections`, leaving room for migrations and admin.
- **Heavy queries scale with the data, not the users.** First 100 of a type, free text and suggestions read more as the model
  grows. A 3.6 M record model is fine for one person (under 1 s) and starts to queue at about 10 people searching at once on
  one 14-CPU database. Models a tenth that size sit in the comfortable zone, as the medium results show.

In short: for models up to hundreds of thousands of records, the architecture is not the limit, and one modest database serves
dozens of people. For millions of records, database CPU is the limit and buying cores helps in proportion, but caching the
schema and counts, and keeping the heaviest searches opt-in, buys more than the next hardware tier.

## Timing a slow Explore load

Explore shows a thin bar over the canvas while it works (opening the page, applying a link or saved view, searching,
expanding). It appears only after 300 ms, so quick loads don't flash it. It logs where the time went, at Information level:

- `Explore first load took ...`: the schema, the history start, and the graph libraries, before any link is applied.
- `Explore restored N nodes and M links: query ... ms, draw and layout ... ms`: the database read, then the browser's
  fcose layout. A view saved with its layout pinned skips the layout, which `(positions pinned)` marks.

The table above times the queries only, so a slow load that is mostly "draw and layout" is the browser, not the database.

## What this does and doesn't show

- **Fine at this size:** expanding, drawing and searching. Expanding and drawing follow indexes, so they barely changed with
  16 times more links than the `rail` tier. The canvas is never the limit, because it only ever draws what's asked for, 100 at
  a time.
- **For the generic store:** read the schema from the definition tables, which removes the sampling, and keep `extra_node`'s plain
  shape. Climbing's Insights queries (the `WITH` clause in `ClimbingInsightsSql`) are written for a logbook's size and have not been measured on a model this big.
- **Not tested:** a real server with real circuits, and writes, including how much the indexes slow them. Several users at once is
  measured below, for reads only. The busiest stop
  measured is the busiest one named Zürich HB, so a stop with more links may exist. The link columns are indexed here by the
  loader, which a real climbing logbook's are not.

## The everyday-use queries

Measured by hand in `psql` on 13 November 2026 against the same 3.6M nodes, not by the performance test:

| What | Time |
| --- | --- |
| Home: records added per month (`HomeRepository.GrowthAsync`) | 2.3 s. It reads every record, so the page draws first and asks for it afterwards. A model can hide it with `Home:Hide` |
| Home: recent records, changes, imports | Read the audit trail and import batches by id, so they don't depend on the record count |
| Cleanup: duplicate names across four types | 2.3 s |
| Cleanup: records with no links, across four types | 9.1 s, 67 ms for one small type. It is on demand, and a type narrows it |
| Path search: one level from 5,000 stops | 350 ms. Each level is capped at 5,000 records and 50,000 links, and the search says when it was cut short |

## Row security on the large sample

Measured on 14 November 2026 by hand in `psql`, on a copy of the sample (`swiss_rail_sec`, cloned with `CREATE DATABASE ...
TEMPLATE swiss_rail`, migrated, and given row security with the real `EnableAsync` script). Labels are made up: 12% of
nodes above the lowest level, 5% restricted to one department, 3% needing one project, and 1% of links above the lowest
level. Each query ran three times, with JIT off, and the table gives the median in ms. "Owner" is the table owner, whom
the policies don't apply to, so it stands for security off. "Wide" is a viewer who sees everything and "narrow" one
who sees about 80% of nodes. Parallel query was off for every run, because the container's shared memory is too small for it.

| What | Owner | Wide viewer | Narrow viewer |
| --- | --- | --- | --- |
| Node counts by type | 422 | 2,700 | 2,050 |
| Name contains, within a type | 50 | 75 | 57 |
| Free text, first 100 | 22 | 1 | 1 |
| Expand a hub stop, first 100 links | 0 | 1 | 1 |
| Links of that stop, counted | 0 | 1 | 1 |
| **First 100 of a type, by name** | **1** | **0.7** | **0.9** |

Expanding, counting a stop's links and searching are unaffected, because they follow indexes and the policy is a cheap filter.
Two things are not fine for a restricted viewer:

- **First 100 of a type, by name** went from 1 ms to about 1 s (7 s cold) with the first version of the policy. It wrapped the
  viewer's settings in `(select ...)`, which hides them from the planner; it guessed almost no row passed, and chose to read the
  whole table and sort it. The node policy is now unwrapped, so the planner reads the settings while planning. Measured again
  the same way: 0.5 to 1 ms for both viewers, using the index. Databases that already have security on get the new policy
  when security is switched on again (`model security on`).
- **Node counts** are 3 to 6 times slower (about 2 to 2.7 s after the change, up from 1.5 to 1.8 s), since the count has to test
  every row, now reading the settings for each. It's a full scan either way, and the unrestricted figure comes from a cheaper path.
  Name-contains in a type went from about 36 to about 57 ms for the same reason.

Not covered: the app's own queries with a viewer (these are the views' shapes by hand, not `GraphRepository`), several users
at once, writes, and hidden fields (those are masked in code, so they cost nothing in the database). The labels on links are
few, so a model that labels most links will cost more on expand.

## Per-record property labels

A record can carry a label on one property (`node_property_label`). The extra read is one primary-key lookup for the records a
page returns: about 1 ms for 100 rows, and 9 to 26 ms for a 5,000-row read. A model with no overrides skips it entirely, and
filtering and search stayed within noise. Only while overrides exist, SQL that reads values uses a derived table with the hidden
keys removed (`props - failing keys`) and the stored search column is bypassed for a secured viewer, so those queries lose their
text index until the overrides are removed.
