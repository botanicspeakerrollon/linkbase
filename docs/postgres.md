# Looking inside Postgres

How to get into the databases, find your way around the tables, and query them by hand. Everything here was run against
the real databases while writing it.

## Which database is where

| Container | Port | Databases | What's in it |
| --- | --- | --- | --- |
| `linkbase-db-1` | 127.0.0.1:5432 | `linkbase` | Your real climbing logbook. Backed up by `scripts/backup-db.ps1` |
| `linkbase-samples` | 127.0.0.1:55433 | `consultancy`, `swiss_rail`, `climbing_demo` | The sample models. Not backed up: they're rebuilt from their model files |

Both only listen on this PC. Each model in the app's switcher is one of these databases (see [models](models.md)).

## Getting in

`psql` is already inside each container, so there's nothing to install:

```powershell
docker exec -it linkbase-db-1 psql -U linkbase -d linkbase
```

```powershell
docker exec -it linkbase-samples psql -U linkbase -d swiss_rail
```

**On the real database, start read-only.** Then nothing you type can change it, however it goes wrong:

```powershell
docker exec -it -e PGOPTIONS="-c default_transaction_read_only=on" linkbase-db-1 psql -U linkbase -d linkbase
```

A write then fails with `cannot execute ... in a read-only transaction`. If you do mean to change something, back up first
(`./scripts/backup-db.ps1`), and remember that hand edits skip the app's rules (see [the end](#changing-data-by-hand)).

A one-off query without opening a session:

```powershell
docker exec linkbase-samples psql -U linkbase -d consultancy -c "SELECT count(*) FROM node"
```

### With a desktop tool

Any Postgres client works, such as DBeaver, pgAdmin or the PostgreSQL extension for VS Code.

- **Real database:** host `127.0.0.1`, port `5432`, database and user `linkbase`. The password is `POSTGRES_PASSWORD` in
  `.env` (see the secrets table in [operations](operations.md#secrets)). Let the tool store it rather than pasting it
  anywhere else, and tick "read-only connection" if the tool has one. `docker exec` doesn't need it.
- **Samples:** host `127.0.0.1`, port `55433`, user `linkbase`, no password, database `consultancy` or `swiss_rail`.

## Finding your way around in psql

| Type | Does |
| --- | --- |
| `\l` | Lists the databases |
| `\c swiss_rail` | Switches to another database in the same container |
| `\dt` | Lists the tables |
| `\dv` | Lists the views |
| `\d node` | Shows a table's columns, indexes and foreign keys |
| `\x` | Turns expanded output on and off: one column per line, good for wide rows |
| `\timing` | Shows how long each query takes |
| `\e` | Opens the last query in an editor |
| `\q` | Quits |

Queries end with `;`. A query that seems stuck is often waiting for that. `Ctrl+C` cancels one that's running.

## The tables

Every database has the same tables, because there's one schema. A model only fills the ones it uses.

**The generic store**, which holds every model, climbing included:

| Table | Holds |
| --- | --- |
| `node_type` | The model's types, such as `Stop` or `Person`, with a colour and icon |
| `hierarchy_level` | The levels of a type whose records form a tree, such as `country`, `region` and `area`, and which each may be in |
| `field_definition` | Each type's fields, and whether each is `text`, `number`, `date` or `boolean` |
| `link_definition` | The relationships allowed, such as `Service CALLS_AT Stop`, with how many links there are in `links` |
| `node` | The records. `id` is the type in lower case, a colon and the key, such as `stop:8503000`. Field values are JSON in `props` |
| `node_link` | The links, from `source_id` to `target_id`, with `rel_type` and any values in `props` |
| `external_ref` | Ids in other systems such as UKC, for a node (`node_id`, `node_type`). It has no foreign key, so what deletes the node deletes its refs |

**Around it:**

| Table | Holds |
| --- | --- |
| `graph_nodes`, `graph_edges` | Views of the generic store as one graph. Explore reads these |
| `climbing_ascent_facts` and the other `climbing_` views | What Insights reads: each ascent with its place and season, trips and partners |
| `graph_scene` | Scenes saved in Explore |
| `import_batch` | Each import, so it can be undone. A CSV import's file name and counts are in `raw_payload` |
| `app_user` | The one user for now |
| `__EFMigrationsHistory` | Which migrations have been applied. Its name needs double quotes |

Names are all lower case with underscores, so `node_type`, never `NodeType`.

## Queries to start from

### Anything

How many of each type there are, in any model:

```sql
SELECT label, count(*) FROM graph_nodes GROUP BY label ORDER BY count(*) DESC;
```

Which migrations the database has:

```sql
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 5;
```

### Your climbing

The latest ascents:

```sql
SELECT a.date, r.name AS route, r.grade, c.name AS crag, a.style
FROM ascent a
JOIN route r ON r.id = a.route_id
JOIN crag c ON c.id = r.crag_id
ORDER BY a.date DESC
LIMIT 10;
```

Who you climb with most:

```sql
SELECT p.name, count(*) AS ascents
FROM partner p
JOIN ascent_partner ap ON ap.partner_id = p.id
GROUP BY p.name
ORDER BY ascents DESC
LIMIT 10;
```

Ascents per year, and your most visited crags:

```sql
SELECT extract(year FROM date)::int AS year, count(*) FROM ascent GROUP BY 1 ORDER BY 1 DESC;

SELECT c.name, count(*) AS ascents
FROM crag c JOIN route r ON r.crag_id = c.id JOIN ascent a ON a.route_id = r.id
GROUP BY c.name ORDER BY ascents DESC LIMIT 10;
```

Trips abroad, newest first. Sort by `sort_date`, because `start_date` is empty when the dates aren't known:

```sql
SELECT name, start_date, end_date FROM trip WHERE abroad ORDER BY sort_date DESC LIMIT 10;
```

### A model's records (the example consultancy)

Connect to `consultancy` first. A field is read from `props` with `->>`, which gives text:

```sql
SELECT key, name, props->>'title' AS title, props->>'joined' AS joined
FROM node WHERE type = 'Person' ORDER BY name;
```

Cast a number field before comparing it, or it compares as text, and `'9' > '100000'`:

```sql
SELECT name, (props->>'budget')::numeric AS budget
FROM node
WHERE type = 'Project' AND (props->>'budget')::numeric > 50000
ORDER BY budget DESC;
```

Everything one record links to:

```sql
SELECT l.rel_type, t.type, t.name
FROM node_link l JOIN node t ON t.id = l.target_id
WHERE l.source_id = 'person:p4';
```

Two steps away: who works on a project for a given client:

```sql
SELECT DISTINCT p.name AS person, c.name AS client
FROM node_link w
JOIN node_link f ON f.source_id = w.target_id AND f.rel_type = 'FOR'
JOIN node p ON p.id = w.source_id
JOIN node c ON c.id = f.target_id
WHERE w.rel_type = 'WORKS_ON' AND c.key = 'c2';
```

What the model says it has, and how many links of each kind:

```sql
SELECT node_type_name, key, type FROM field_definition ORDER BY node_type_name, id;
SELECT source_type, rel_type, target_type, links FROM link_definition ORDER BY links DESC;
```

### The large sample (Swiss public transport)

Connect to `swiss_rail`, and turn on `\timing`. Queries that start from a record are quick; ones that read a whole table
aren't.

Zürich HB and its platforms. Platforms are their own stops, with keys such as `8503000:10`:

```sql
SELECT key, name, props->>'platform' AS platform
FROM node WHERE type = 'Stop' AND name = 'Zürich HB' ORDER BY key LIMIT 10;
```

The first departures from platform 10, in about 40 ms:

```sql
SELECT s.name AS towards, s.props->>'short_name' AS service, c.props->>'departure' AS departs
FROM node_link c JOIN node s ON s.id = c.source_id
WHERE c.target_id = 'stop:8503000:10' AND c.rel_type = 'CALLS_AT'
ORDER BY c.props->>'departure'
LIMIT 10;
```

Every stop of one of those services, in order:

```sql
SELECT t.name AS stop, (c.props->>'sequence')::int AS seq, c.props->>'departure' AS departs
FROM node_link c JOIN node t ON t.id = c.target_id
WHERE c.rel_type = 'CALLS_AT'
  AND c.source_id = (SELECT source_id FROM node_link WHERE target_id = 'stop:8503000:10' AND rel_type = 'CALLS_AT' LIMIT 1)
ORDER BY seq;
```

Stations in a box of latitude and longitude, around Zermatt:

```sql
SELECT name, props->>'lat' AS lat, props->>'lon' AS lon
FROM node
WHERE type = 'Stop' AND key NOT LIKE '%:%'
  AND (props->>'lat')::numeric BETWEEN 46.0 AND 46.03
  AND (props->>'lon')::numeric BETWEEN 7.73 AND 7.76;
```

The operators with the most lines. This reads all 45M links, so it takes about 12 seconds:

```sql
SELECT a.name AS agency, l.lines
FROM (SELECT target_id, count(*) AS lines FROM node_link WHERE rel_type = 'RUN_BY'
      GROUP BY target_id ORDER BY lines DESC LIMIT 10) l
JOIN node a ON a.id = l.target_id
ORDER BY l.lines DESC;
```

### Why a query is slow

Put `EXPLAIN ANALYZE` in front of it. `Index Scan` means it went straight to the rows; `Seq Scan` on `node` or `node_link`
means it read the whole table, which is fine on the example and slow on Swiss. [Performance](performance.md) has the
lessons learned so far.

```sql
EXPLAIN ANALYZE SELECT count(*) FROM node_link WHERE target_id = 'stop:8503000:10';
```

## Changing data by hand

Prefer the app, which keeps everything consistent. A hand edit skips these rules:

- `link_definition.links` counts the links. Adding or removing rows in `node_link` without updating it makes the home page and
  the graph model wrong.
- `updated_at` isn't stamped by Postgres. Set it yourself, or the climbing import's undo may treat an edited row as unedited.
- Foreign keys never cascade, so deleting something still referenced fails. That's on purpose.
- `trip` is rebuilt from ascents, so hand edits there don't last.

If you must, do it in a transaction and look before committing:

```sql
BEGIN;
UPDATE node SET name = 'Zürich Hauptbahnhof', updated_at = now() WHERE id = 'stop:8503000';
SELECT id, name FROM node WHERE id = 'stop:8503000';
ROLLBACK;  -- or COMMIT; when it's right
```
