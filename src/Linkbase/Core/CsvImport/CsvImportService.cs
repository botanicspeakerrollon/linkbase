using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsvHelper;
using CsvHelper.Configuration;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.CsvImport;


// Reads a CSV file of records or links and stages what it would add, for review and commit by ImportStagingService, or
// imports it straight away, which is staging and committing in one go. Records whose key is already there are left as they
// are, so undoing an import only ever removes what it added. See docs/models.md.
public sealed partial class CsvImportService(IDbContextFactory<LinkbaseDbContext> contextFactory, RecordHooks? hooks = null)
{
    private readonly RecordHooks _hooks = hooks ?? RecordHooks.None;
    private readonly ImportStagingService _staging = new(contextFactory, hooks);

    public const long MaxBytes = 100L * 1024 * 1024;
    private const int Samples = 5;
    private const int GuessRows = 1000;
    private const int ProblemsShown = 10;
    private const int PreviewRows = 5;

    public async Task<CsvImportPlan> PlanAsync(CsvFile file, CsvRecordMapping mapping, CancellationToken cancellationToken = default)
    {
        var (rows, problems, notes, skipped, _) = await ReadRecordsAsync(file, mapping, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await ExistingKeysAsync(db, mapping.Type, rows.Select(r => r.Key).ToList(), cancellationToken);
        return new CsvImportPlan(file.Rows, rows.Count(r => !existing.Contains(r.Key)), rows.Count(r => existing.Contains(r.Key)), skipped, problems, notes);
    }

    public async Task<CsvImportPlan> PlanAsync(CsvFile file, CsvLinkMapping mapping, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var (pairs, problems, notes, skipped) = await ReadLinksAsync(db, file, mapping, cancellationToken);
        var existing = await ExistingLinksAsync(db, mapping.RelType, pairs, cancellationToken);
        return new CsvImportPlan(file.Rows, pairs.Count - existing.Count, existing.Count, skipped, problems, notes);
    }

    public async Task<CsvImportResult> ImportAsync(CsvFile file, CsvRecordMapping mapping, CancellationToken cancellationToken = default) =>
        await CommitAsync(await StageAsync(file, mapping, cancellationToken), cancellationToken);

    public async Task<CsvImportResult> ImportAsync(CsvFile file, CsvLinkMapping mapping, CancellationToken cancellationToken = default) =>
        await CommitAsync(await StageAsync(file, mapping, cancellationToken), cancellationToken);

    private async Task<CsvImportResult> CommitAsync(long batch, CancellationToken cancellationToken)
    {
        var committed = await _staging.CommitAsync(batch, cancellationToken);
        return new CsvImportResult(batch, committed.Added);
    }

    // Works out which records the file would add and stages them, for review. Nothing else changes until the batch is
    // committed.
    public async Task<long> StageAsync(CsvFile file, CsvRecordMapping mapping, CancellationToken cancellationToken = default)
    {
        var (rows, problems, notes, skipped, choices) = await ReadRecordsAsync(file, mapping, cancellationToken);
        Stop(problems);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await ExistingKeysAsync(db, mapping.Type, rows.Select(r => r.Key).ToList(), cancellationToken);

        // A module's hooks set the fields it keeps, so they're staged with the record.
        if (_hooks.Watch(mapping.Type))
        {
            var type = await new RecordRepository(contextFactory).TypeAsync(mapping.Type, cancellationToken)
                ?? new RecordType(mapping.Type, null, null, mapping.Fields.Select(f => new RecordField(f.Key, f.Type)).ToList());
            rows = rows.Select(row =>
            {
                var props = JsonNode.Parse(row.Props) as JsonObject ?? [];
                _hooks.BeforeSave(type, row.Name, props);
                return row with { Props = props.ToJsonString() };
            }).ToList();
        }

        var added = rows.Where(r => !existing.Contains(r.Key)).ToList();
        var model = new ModelChanges(
            [mapping.Type],
            mapping.Fields.Select(f => new StagedField(mapping.Type, f.Key, f.Type, choices.TryGetValue(f.Key, out var options) ? options : [])).ToList(),
            []);
        return await _staging.StageAsync(
            new StagingRequest(DataSource.Csv, file.FileName, $"{mapping.Type} records", model, rows.Count - added.Count, skipped, notes),
            added.Select(r => new StagedRecord(mapping.Type, r.Key, r.Name, r.Props)),
            [],
            cancellationToken);
    }

    public async Task<long> StageAsync(CsvFile file, CsvLinkMapping mapping, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var (pairs, problems, notes, skipped) = await ReadLinksAsync(db, file, mapping, cancellationToken);
        Stop(problems);

        var existing = await ExistingLinksAsync(db, mapping.RelType, pairs, cancellationToken);
        var model = new ModelChanges([], [], [new StagedRelationship(mapping.FromType, mapping.RelType, mapping.ToType)]);
        return await _staging.StageAsync(
            new StagingRequest(DataSource.Csv, file.FileName, $"{mapping.FromType} {mapping.RelType} {mapping.ToType} links", model, existing.Count, skipped, notes),
            [],
            pairs.Where(p => !existing.Contains(p)).Select(p => new StagedLink(mapping.FromType, mapping.RelType, p.Source, mapping.ToType, p.Target)),
            cancellationToken);
    }

    public Task UndoAsync(long batchId, CancellationToken cancellationToken = default) => _staging.UndoAsync(batchId, cancellationToken);

    public async Task<IReadOnlyList<CsvBatch>> RecentAsync(int count, CancellationToken cancellationToken = default) =>
        (await _staging.RecentAsync(count, cancellationToken))
        .Select(b => new CsvBatch(b.Id, b.File, b.What, b.Status == ImportStatus.Staged ? b.Items : b.Added, b.Status, b.At))
        .ToList();
}
