using System.Collections.Concurrent;
using System.Data.Common;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Linkbase.Core.Security;

// Turns Postgres row-level security on or off for one model's database. It has to run as the database owner, so callers pass
// a context they made themselves (as `model` commands do), never one from the circuit's factory. Everything is in one
// transaction, so a failed step can't leave security on with no policy. See docs/security-design.md, "Postgres mechanics".
public static class RowSecurity
{
    // The role viewers' queries run as. It's a plain role that can't bypass policies; the connection sets it per query.
    public const string AppRole = "linkbase_app";

    // Tables with a label of their own, and the views over them that must run with the caller's rights.
    private static readonly string[] Views = ["graph_nodes", "graph_edges"];

    public static async Task<bool> IsEnabledAsync(LinkbaseDbContext db, CancellationToken cancellationToken = default) =>
        await db.Database.SqlQuery<bool>($"select coalesce((select relrowsecurity from pg_class where oid = 'public.node'::regclass), false) as \"Value\"")
            .SingleAsync(cancellationToken);

    public static async Task EnableAsync(LinkbaseDbContext db, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(Escape(EnableScript), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        RowSecurityState.Forget();
    }

    public static async Task DisableAsync(LinkbaseDbContext db, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(Escape(DisableScript), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        RowSecurityState.Forget();
    }

    // ExecuteSqlRaw treats braces as format placeholders, and the scripts have array literals.
    private static string Escape(string sql) => sql.Replace("{", "{{").Replace("}", "}}");

    // The helper functions read the viewer from session settings. Wrapped in (select ...) they are evaluated once per
    // statement, not per row, which suits the link table (big, reached through indexes on its ends, where the planner's guess
    // doesn't matter). The node policy is left unwrapped, so the planner can see the viewer. The test is written out in each policy: wrapped in a function of its own, Postgres can't inline
    // it, and it measured twelve times slower because the settings were read for every row. There are no GIN indexes on the
    // label arrays: with most rows unrestricted they aren't selective, and the planner scanned them whole (docs/performance.md).
    private static readonly string EnableScript = $$"""
        create schema if not exists linkbase_sec;

        do $role$ begin
          if not exists (select from pg_roles where rolname = '{{AppRole}}') then
            create role {{AppRole}} nologin;
          end if;
        end $role$;
        grant {{AppRole}} to current_user;

        create or replace function linkbase_sec.viewer_level() returns int language sql stable parallel safe as
          $f$ select coalesce(nullif(current_setting('linkbase.level', true), '')::int, -1) $f$;
        create or replace function linkbase_sec.viewer_any() returns text[] language sql stable parallel safe as
          $f$ select coalesce(nullif(current_setting('linkbase.any', true), '')::text[], '{}') $f$;
        create or replace function linkbase_sec.viewer_all() returns text[] language sql stable parallel safe as
          $f$ select coalesce(nullif(current_setting('linkbase.all', true), '')::text[], '{}') $f$;
        grant usage on schema linkbase_sec to {{AppRole}};
        grant usage on schema public to {{AppRole}};
        grant select, insert, update, delete on all tables in schema public to {{AppRole}};
        grant usage, select on all sequences in schema public to {{AppRole}};
        alter default privileges in schema public grant select, insert, update, delete on tables to {{AppRole}};
        alter default privileges in schema public grant usage, select on sequences to {{AppRole}};

        -- Not wrapped in (select ...), unlike the link policy below: the planner reads a stable function's value while planning,
        -- so it knows how many rows pass and walks the (type, name) index for "first 100". Wrapped, the value is hidden, the
        -- planner guesses almost nothing passes, and it sorts the whole table instead (a second, not a millisecond).
        drop policy if exists linkbase_node on node;
        create policy linkbase_node on node for all to {{AppRole}}
          using (label_level <= linkbase_sec.viewer_level() and label_any && linkbase_sec.viewer_any() and label_all <@ linkbase_sec.viewer_all())
          with check (label_level <= linkbase_sec.viewer_level() and label_any && linkbase_sec.viewer_any() and label_all <@ linkbase_sec.viewer_all());

        -- A link shows only if its own label passes and both its records are visible (node's policy filters the subqueries).
        drop policy if exists linkbase_node_link on node_link;
        create policy linkbase_node_link on node_link for all to {{AppRole}}
          using (label_level <= (select linkbase_sec.viewer_level()) and label_any && (select linkbase_sec.viewer_any()) and label_all <@ (select linkbase_sec.viewer_all())
                 and exists (select 1 from node s where s.id = source_id)
                 and exists (select 1 from node t where t.id = target_id))
          with check (label_level <= (select linkbase_sec.viewer_level()) and label_any && (select linkbase_sec.viewer_any()) and label_all <@ (select linkbase_sec.viewer_all())
                 and exists (select 1 from node s where s.id = source_id)
                 and exists (select 1 from node t where t.id = target_id));

        -- Where a node came from is visible only with the node.
        drop policy if exists linkbase_external_ref on external_ref;
        create policy linkbase_external_ref on external_ref for all to {{AppRole}}
          using (exists (select 1 from node n where n.id = node_id))
          with check (exists (select 1 from node n where n.id = node_id));

        alter view graph_nodes set (security_invoker = true);
        alter view graph_edges set (security_invoker = true);

        alter table node enable row level security;
        alter table node_link enable row level security;
        alter table external_ref enable row level security;
        """;

    private static readonly string DisableScript = """
        alter table external_ref disable row level security;
        alter table node_link disable row level security;
        alter table node disable row level security;
        alter view graph_nodes set (security_invoker = false);
        alter view graph_edges set (security_invoker = false);
        drop policy if exists linkbase_external_ref on external_ref;
        drop policy if exists linkbase_node_link on node_link;
        drop policy if exists linkbase_node on node;
        drop index if exists ix_node_label_any, ix_node_label_all, ix_node_link_label_any, ix_node_link_label_all;
        drop schema if exists linkbase_sec cascade;
        """;
}

// Whether a database has row security on and what its security section says, remembered per connection string and re-read
// every half minute, so a change made by another instance takes effect soon. With security off, a connection opening costs one
// dictionary lookup.
public sealed class RowSecurityState(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private static long _generation;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private sealed record Entry(bool Enabled, ModelSecurity? Security, DateTimeOffset Expires, long Generation);

    // A change made here is seen at once by every state in this process.
    public static void Forget() => Interlocked.Increment(ref _generation);

    // Null when row security is off. The connection is the one just opened, still unrestricted.
    public async ValueTask<ModelSecurity?> SecurityAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var key = connection.ConnectionString;
        var now = time.GetUtcNow();
        if (_entries.TryGetValue(key, out var entry) && entry.Expires > now && entry.Generation == Interlocked.Read(ref _generation))
        {
            return entry.Enabled ? entry.Security : null;
        }

        var generation = Interlocked.Read(ref _generation);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select coalesce((select relrowsecurity from pg_class where oid = 'public.node'::regclass), false),
                   (select security::text from model_setting limit 1)
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var enabled = reader.GetBoolean(0);
        var security = reader.IsDBNull(1) ? null : LabelCodec.FromJson(reader.GetString(1));
        _entries[key] = new Entry(enabled, security, now + Lifetime, generation);
        return enabled ? security ?? new ModelSecurity() : null;
    }
}

// Who is asking, resolved against a model's security section. Sign-in will say who; until then it's the configured user.
public interface IViewerResolver
{
    ValueTask<Viewer> ResolveAsync(ModelSecurity security, CancellationToken cancellationToken = default);
}

// Tells the database who is asking, each time a connection opens, and drops the connection to a role that policies bind.
// Postgres resets both when the connection goes back to the pool. Does nothing when row security is off.
public sealed class ViewerConnectionInterceptor(RowSecurityState state, IViewerResolver resolver) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (connection is not NpgsqlConnection npgsql)
        {
            return;
        }

        if (await state.SecurityAsync(npgsql, cancellationToken) is { } security)
        {
            await Apply(npgsql, await resolver.ResolveAsync(security, cancellationToken), security, cancellationToken);
        }
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        ConnectionOpenedAsync(connection, eventData).GetAwaiter().GetResult();

    // A part the model doesn't use is never checked, so it's given values that always pass: the highest level, and the
    // unrestricted marker. A nobody has -1, which fails a model that uses levels.
    private static async Task Apply(NpgsqlConnection connection, Viewer viewer, ModelSecurity security, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            select set_config('linkbase.level', @level, false), set_config('linkbase.any', @any, false), set_config('linkbase.all', @all, false);
            set role {RowSecurity.AppRole};
            """;
        command.Parameters.AddWithValue("level", (security.HasLevel ? viewer.Level : short.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("any", LabelCodec.ToArrayLiteral([LabelCodec.Everyone, .. viewer.AnyOf]));
        command.Parameters.AddWithValue("all", LabelCodec.ToArrayLiteral(viewer.AllOf));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
