using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Records;

// Lists, shows and edits the generic store's data, guided by the model's definitions. See docs/models.md.
// Domain modules' hooks run on every change made through it, except ReplaceDerivedAsync, which is the modules' own.
public sealed partial class RecordRepository(IDbContextFactory<LinkbaseDbContext> contextFactory, RecordHooks? hooks = null)
{
    public const int LinksShown = 25;

    private readonly RecordHooks _hooks = hooks ?? RecordHooks.None;
}
