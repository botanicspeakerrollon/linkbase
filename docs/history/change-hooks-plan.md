# Change hooks plan

Steps 1 (the events) and 2 in part (a basic dispatcher with the `console` hook) are built; the HTTP hook, back-off, `hooks` commands, metrics and the rest are not. This plans INT-1 and INT-2 in [requirements](../requirements.md#311-integration-int): telling other systems,
such as a message bus, a webhook or an integration platform, that records or links changed. The read API (INT-3, built: see [api-plan](api-plan.md)) is a
separate plan, but this one leans on it: events say *what* changed, and a consumer that wants the values reads them through
the API as itself.

## Decisions

- **An outbox, not code in the transaction.** The change and a `change_event` row commit together. A dispatcher reads
  committed events afterwards and hands them to hooks. A slow or broken bus can then never slow a save, roll it back, or hear
  about a change that rolled back.
- **Written by `ChangeRecorder`.** Every write path already goes through it, and `ChangeRecorderCoverageTests` already fails
  when one doesn't. One event per change set, so an import is one event (as HIS-7), not one per record.
- **Written whether or not history is on.** Today callers skip the recorder when `history.record` is false. That check moves
  inside the recorder: history rows only when recording, the event always.
- **Thin events.** An event names the records and links affected (a node's id carries its type key, in lower case), the operation and the keys of the fields that
  changed. No values and no reason text. Values are read through the API, as the consumer's own identity, so row and field
  security stay in one place and a bus never holds a copy of a hidden field.
- **At least once, in commit order, per hook.** Each hook has its own position. A hook that fails retries the same events with
  back-off and doesn't hold up the others. Consumers drop repeats by the event id.
- **Hooks are code, chosen by configuration.** One interface and a registry, like `LlmProviders`: a new kind of hook is one
  class and one line. The first built-in kind posts to an HTTP address. Bus kinds (Kafka, Service Bus, RabbitMQ) each need a
  NuGet package, so each is asked for separately.
- **Not `IRecordHook`.** `IRecordHook` runs inside the app, can refuse a change, and its `AfterChangeAsync` runs after commit
  in memory, so it's lost if the process stops. A change hook runs after commit, can't refuse anything and survives a restart.

## The table

One per model database, in a migration (so `model migrate` adds it to every model; ask before running it on the real one).

| Column | Holds |
| --- | --- |
| `id` | `bigint` identity. Its order is commit order (see [Ordering](#ordering)). |
| `change_set_id` | The change set, when history is on; null when it's off. A foreign key that, like all of them, never cascades. |
| `kind` | The justification kind: `manual`, `import`, `derived`, `correction`. |
| `import_batch_id` | The batch, for an import commit or undo. |
| `cause_change_set_id` | For derived work (a trip rebuild), the change set that caused it (null when history is off). |
| `entities` | `jsonb`: up to 1,000 entries of `{kind, id, source?, rel?, target?, op, fields?}`, one per thing changed (a field edit is one entry listing its keys, not one per field). |
| `entity_count` | How many things changed in all. More than `entities` holds means "too many to list": read the batch or the model again. |
| `created_at` | When it was written. |

`hook_position (hook_key, last_event_id, attempts, next_attempt_at, last_error, updated_at)` holds each hook's place.

Ids, types and field keys are not sensitive values, so `entities` as JSON doesn't break the rule about sensitive fields.

## Writing

- `ChangeRecorder.WriteAsync` takes the history settings. It writes the change set and its changes when recording, then the
  event in all cases. The `settings.Record ? WriteAsync(...) : null` pattern goes from the 15 call sites, and the coverage
  test fails if it comes back.
- Callers build their `PendingChange` list even when history is off. Measure that on `swiss_rail`, whose history is off.
- The same statement runs `NOTIFY linkbase_change_event`, which Postgres only delivers on commit, so the dispatcher wakes
  when there's something to read.
- **Imports** list the batch, the updated records and links, and the new types, fields and relationships. Created records
  aren't listed (they're found by `import_batch_id`, through the API). **Model load** (MOD-8) is one event naming the model:
  everything was replaced, so read it again.
- **Derived rebuilds** already run in their own transaction after the cause commits, so they're their own event, pointing at
  the cause.
- **Definition and label changes** already go through the recorder, so they raise events too, with `kind` `definition` or a
  `labels` field key.
- **The one exemption, now gone.** `ClimbingRecordHook` renames ascents after their route. It does so in its own
  transaction and raises a separate `derived` event for the ascents (not part of the route's event, which has already
  committed), so a consumer that follows ascents hears of it. `ChangeRecorderCoverageTests` no longer exempts it.

## Ordering

Ids from a sequence are handed out before commit, so two saves at once can commit in the opposite order to their ids, and a
dispatcher that reads "after the last id" would skip one for good. So the event insert first takes
`pg_advisory_xact_lock` on a per-model key. That lock is held until commit, so the next writer's event gets a higher id
*and* commits later: ids are in commit order and nothing is skipped.

The cost: writers queue only between writing their event and committing, which is the tail of each transaction (in the
paths read so far the recorder is called just before commit; step 1 checks every one and moves any that aren't). That's fine for people editing. It's measured with writes added to
`LoadTests` before this is called done. If it ever isn't fine, the fallback is reading only events whose transaction is
older than the oldest one still running (`pg_snapshot_xmin`), which loses strict order but never skips.

## Dispatching

- A `BackgroundService` per model, in processes whose role includes `worker` (`Linkbase:Roles`; the default `all` keeps
  one process as today, INT-4). It's the app's first background service.
- **One dispatcher per model**, whatever the number of replicas: it holds `pg_try_advisory_lock` on its own connection, and the
  others wait. If that process stops, the lock goes with its connection and another takes over.
- It wakes on the `NOTIFY`, or every 5 seconds anyway. For each hook that includes the model and is due, it reads up to that
  hook's `BatchSize` events after `last_event_id` and calls the hook. On success it moves the position on. On failure it
  records the error and tries again after 1 s, 2 s, 4 s … up to 5 minutes, and keeps trying. It never skips on its own.
- **A new hook** starts at the newest event, or at the oldest still kept with `StartFrom: oldest`.
- **Clean-up** deletes events older than `Hooks:RetentionDays` (7) that every hook on the model has passed. An event a hook
  hasn't taken is never deleted.

## Hooks

```csharp
public interface IChangeHook
{
    Task HandleAsync(IReadOnlyList<ChangeEvent> events, CancellationToken cancellationToken);  // throw to retry
}

public abstract class ChangeHookKind
{
    public abstract string Id { get; }   // "http"
    public abstract IChangeHook Create(string key, IConfigurationSection settings, IServiceProvider services);
}
```

`ChangeHookKinds.All` lists the kinds, as `LlmProviders.All` does. Configuration names the hooks:

```json
"Hooks": {
  "erp": { "Kind": "http", "Models": ["projects"], "Url": "https://integration.example.test/linkbase", "BatchSize": 1, "TimeoutSeconds": 10 }
}
```

Each hook's secret (`Hooks__erp__Secret`) gets a row, by name, in the Secrets table in [operations](../operations.md).
Start-up fails, with the reason, if a hook names an unknown kind or model.

### The HTTP hook

- `POST` with `Content-Type: application/cloudevents+json` (`application/cloudevents-batch+json` for more than one). Any 2xx
  is success, anything else is retried.
- `Linkbase-Signature: t=<unix time>,v1=<HMAC-SHA256 of "t.body" with the hook's secret>`, so the receiver can check it came
  from us and isn't a replay.
- Hook addresses are the operator's configuration, not something a visitor typed, so SEC-10's ban on private addresses doesn't
  apply. An internal bus is usually on a private address.

### The event

A CloudEvents 1.0 envelope: a standard shape any bus understands, and no package needed.

```json
{
  "specversion": "1.0",
  "type": "linkbase.changes.v1",
  "source": "/linkbase/models/projects",
  "id": "projects:1234",
  "time": "2026-11-16T10:00:00Z",
  "datacontenttype": "application/json",
  "data": {
    "model": "projects", "event": 1234, "changeSet": 987, "kind": "manual", "importBatch": null, "cause": null,
    "count": 2,
    "entities": [
      { "kind": "node", "id": "p-17", "type": "Project", "op": "update", "fields": ["status"] },
      { "kind": "link", "source": "p-17", "rel": "led_by", "target": "e-4", "op": "create" }
    ]
  }
}
```

`id` is stable, so a consumer drops repeats by it. A change to this shape is a new `type` (`linkbase.changes.v2`).

## Security

The dispatcher reads as the database owner, so every hook hears of every change, including records some viewers can't see,
by id and type. A hook is trusted like a database replica, and the guide for operators says so. Filtering a hook's events by an
identity of its own is a later step, if a model with row security needs it.

## Seeing it work

- `hooks status <model>` (a command, like `model migrate`): each hook, its position, how far behind it is, attempts and the last error.
- `hooks skip <model> <hook> <event>` and `hooks replay <model> <hook> <event>`: move a position by hand, written to the log.
- OpenTelemetry (OPS-8): `linkbase.hooks.lag` (events behind), `linkbase.hooks.failures`, a span per delivery.
- Hooks being behind doesn't make the app unready.

## Steps

1. **Done.** Migration, the table, `ChangeRecorder` writing events always (history check moved inside), the lock and `NOTIFY`, the
   climbing rename. Database tests: event with the change, none on rollback, written with history off, ids in commit order
   with writers at once, one event per import. Cost on `swiss_rail` (history off, so callers now build their
   `PendingChange` lists there): analysed, not timed end to end. Imports list only updates, deletes and new definitions
   (creates aren't listed), which the commit already holds in memory, and a model load builds one change; grouping is
   linear in the list and only 1,000 entries are serialised. Each write adds one row and an advisory lock held to commit.
   Time an import commit with updates on the large sample when the writes are added to `LoadTests`.
2. **Basic version done** (`Core/Hooks`, table `hook_position`): `IChangeHook`, `ChangeHookKind`, `ChangeHookKinds` with a `console` kind that writes JSON lines to stdout,
   and a dispatcher polling every 5 seconds. A transaction per hook and model holds `pg_try_advisory_xact_lock` while it reads, calls the
   hook and moves the position, which does the leader lock's job. Left out for now: NOTIFY wake-up, back-off (it retries every pass), `Linkbase:Roles`,
   `StartFrom`, clean-up, attempts and last error in `hook_position`. Planned in full: `IChangeHook`, `ChangeHookKinds`, the dispatcher with its leader lock, positions, retry and clean-up, `Linkbase:Roles`,
   and a fake hook in tests: retry, back-off, a broken hook not holding up another, two dispatchers with one working.
3. The HTTP hook with its signature, tested against a fake handler; `hooks status|skip|replay`; metrics.
4. Docs: operations (secrets, adding a hook), models, reference, architecture, and INT-1 and INT-2 set to Met.

Later, each asked for: bus hooks (a package each), hooks filtered by an identity, and the API paging a batch's records (INT-3).

## Open questions

- Should events carry the reason note? Planned: no, because it's free text a person wrote.
- Should a hook ever give up on an event and move on (a dead-letter table after N attempts)? Planned: no, it keeps trying and
  the operator skips it, because moving on silently breaks the order.
- Events are written even when no hook is configured, so a hook added later can replay the last week. The cost is one row per
  change set. Planned: yes.
