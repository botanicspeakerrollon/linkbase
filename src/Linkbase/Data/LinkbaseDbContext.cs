using System.Text.Json;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Data;

public class LinkbaseDbContext(DbContextOptions<LinkbaseDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    // The keys that sign cookies and anti-forgery tokens, so a restart or another pod can still read them. Only the
    // default model's database is used for these. See Program.cs.
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<ExternalRef> ExternalRefs => Set<ExternalRef>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<SavedView> SavedViews => Set<SavedView>();
    public DbSet<NodeType> NodeTypes => Set<NodeType>();
    public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();
    public DbSet<LinkDefinition> LinkDefinitions => Set<LinkDefinition>();
    public DbSet<LinkAttribute> LinkAttributes => Set<LinkAttribute>();
    public DbSet<HierarchyLevel> HierarchyLevels => Set<HierarchyLevel>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<NodeLink> NodeLinks => Set<NodeLink>();
    public DbSet<StagedItem> StagedItems => Set<StagedItem>();
    public DbSet<ModelSetting> ModelSettings => Set<ModelSetting>();
    public DbSet<Justification> Justifications => Set<Justification>();
    public DbSet<ChangeSet> ChangeSets => Set<ChangeSet>();
    public DbSet<Change> Changes => Set<Change>();
    public DbSet<GraphNodeRow> GraphNodes => Set<GraphNodeRow>();
    public DbSet<GraphEdgeRow> GraphEdges => Set<GraphEdgeRow>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<AreaKind>().HaveConversion<SnakeCaseEnumConverter<AreaKind>>();
        configurationBuilder.Properties<GradeSystem>().HaveConversion<SnakeCaseEnumConverter<GradeSystem>>();
        configurationBuilder.Properties<Discipline>().HaveConversion<SnakeCaseEnumConverter<Discipline>>();
        configurationBuilder.Properties<AscentStyle>().HaveConversion<SnakeCaseEnumConverter<AscentStyle>>();
        configurationBuilder.Properties<DataSource>().HaveConversion<SnakeCaseEnumConverter<DataSource>>();
        configurationBuilder.Properties<ImportStatus>().HaveConversion<SnakeCaseEnumConverter<ImportStatus>>();
        configurationBuilder.Properties<DatePrecision>().HaveConversion<SnakeCaseEnumConverter<DatePrecision>>();
        configurationBuilder.Properties<StagedKind>().HaveConversion<SnakeCaseEnumConverter<StagedKind>>();
        configurationBuilder.Properties<SavedViewKind>().HaveConversion<SnakeCaseEnumConverter<SavedViewKind>>();
        configurationBuilder.Properties<FieldType>().HaveConversion<SnakeCaseEnumConverter<FieldType>>();
        configurationBuilder.Properties<JustificationKind>().HaveConversion<SnakeCaseEnumConverter<JustificationKind>>();
        configurationBuilder.Properties<ChangeEntityKind>().HaveConversion<SnakeCaseEnumConverter<ChangeEntityKind>>();
        configurationBuilder.Properties<ChangeOp>().HaveConversion<SnakeCaseEnumConverter<ChangeOp>>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<ExternalRef>(externalRef =>
        {
            // node_type is part of the key because a source's ids are only unique per kind of thing: UKC crag 123 and
            // UKC climb 123 are different records.
            externalRef.HasIndex(r => new { r.Source, r.NodeType, r.ExternalId }).IsUnique();
            externalRef.HasIndex(r => r.NodeId);
            externalRef.Property(r => r.NodeId).HasDefaultValue("");
            externalRef.Property(r => r.NodeType).HasDefaultValue("");

            // No foreign key to node: a model reload truncates node, and that would have to truncate the refs too.
        });

        modelBuilder.Entity<ImportBatch>(batch =>
        {
            batch.Property(b => b.RawPayload).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SavedView>(view =>
        {
            view.HasIndex(v => new { v.UserId, v.Name }).IsUnique();
            view.Property(v => v.Pinned).HasDefaultValue(false);
        });

        modelBuilder.Entity<NodeType>(type => type.HasKey(t => t.Name));

        modelBuilder.Entity<HierarchyLevel>(level =>
        {
            level.HasOne(l => l.NodeType).WithMany(t => t.HierarchyLevels).HasForeignKey(l => l.NodeTypeName);
            level.HasIndex(l => new { l.NodeTypeName, l.Kind }).IsUnique();
            level.Property(l => l.IsRoot).HasDefaultValue(false);
        });

        modelBuilder.Entity<FieldDefinition>(field =>
        {
            field.HasOne(f => f.NodeType).WithMany(t => t.Fields).HasForeignKey(f => f.NodeTypeName);
            field.HasIndex(f => new { f.NodeTypeName, f.Key }).IsUnique();
            field.Property(f => f.Required).HasDefaultValue(false);
            field.Property(f => f.Multiple).HasDefaultValue(false);
            field.Property(f => f.LabelLevel).HasDefaultValue((short)0);
            field.Property(f => f.LabelAny).HasDefaultValueSql("'{*}'");
            field.Property(f => f.LabelAll).HasDefaultValueSql("'{}'");
        });

        modelBuilder.Entity<LinkDefinition>(link =>
        {
            link.HasOne(l => l.Source).WithMany().HasForeignKey(l => l.SourceType);
            link.HasOne(l => l.Target).WithMany().HasForeignKey(l => l.TargetType);
            link.HasIndex(l => new { l.SourceType, l.RelType, l.TargetType }).IsUnique();
            link.Property(l => l.Required).HasDefaultValue(false);
            link.Property(l => l.Links).HasDefaultValue(0L);
        });

        modelBuilder.Entity<LinkAttribute>(attribute =>
        {
            attribute.HasOne(a => a.LinkDefinition).WithMany(l => l.Attributes).HasForeignKey(a => a.LinkDefinitionId);
            attribute.HasIndex(a => new { a.LinkDefinitionId, a.Key }).IsUnique();
            attribute.Property(a => a.Required).HasDefaultValue(false);
            attribute.Property(a => a.Multiple).HasDefaultValue(false);
        });

        modelBuilder.Entity<Node>(node =>
        {
            node.HasOne(n => n.NodeType).WithMany().HasForeignKey(n => n.Type);
            node.Property(n => n.Props).HasColumnType("jsonb");
            node.Property(n => n.LabelLevel).HasDefaultValue((short)0);
            node.Property(n => n.LabelAny).HasDefaultValueSql("'{*}'");
            node.Property(n => n.LabelAll).HasDefaultValueSql("'{}'");
            node.Property(n => n.SearchText).HasComputedColumnSql("graph_search_text(name, props)", stored: true);
            node.HasIndex(n => new { n.Type, n.Key }).IsUnique();

            // Lists come in name order, a page at a time, so even a type with millions of records opens at once.
            node.HasIndex(n => new { n.Type, n.Name, n.Key });
            node.HasIndex(n => n.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
            node.HasIndex(n => n.SearchText).HasMethod("gin").HasOperators("gin_trgm_ops");
        });

        modelBuilder.Entity<NodeLink>(link =>
        {
            link.HasOne(l => l.Source).WithMany().HasForeignKey(l => l.SourceId);
            link.HasOne(l => l.Target).WithMany().HasForeignKey(l => l.TargetId);
            link.Property(l => l.Props).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
            link.Property(l => l.LabelLevel).HasDefaultValue((short)0);
            link.Property(l => l.LabelAny).HasDefaultValueSql("'{*}'");
            link.Property(l => l.LabelAll).HasDefaultValueSql("'{}'");
        });

        modelBuilder.Entity<StagedItem>(item =>
        {
            item.Property(i => i.Props).HasColumnType("jsonb");
            item.HasOne(i => i.ImportBatch).WithMany().HasForeignKey(i => i.ImportBatchId);

            // Reviewing lists a batch's items a page at a time, and committing reads them in order.
            item.HasIndex(i => new { i.ImportBatchId, i.Kind, i.Id });
        });

        modelBuilder.Entity<ModelSetting>(setting =>
        {
            setting.Property(s => s.Id).ValueGeneratedNever();
            setting.Property(s => s.RecordHistory).HasDefaultValue(true);
            setting.Property(s => s.RequireReason).HasDefaultValue(true);
            setting.Property(s => s.SourceKinds).HasDefaultValueSql("'{}'");
            setting.Property(s => s.Security).HasColumnType("jsonb");
            setting.ToTable(t => t.HasCheckConstraint("ck_model_setting_one_row", "id = 1"));
        });

        modelBuilder.Entity<Justification>(justification =>
        {
            justification.Property(j => j.CreatedAt).HasDefaultValueSql("now()");
            justification.HasOne(j => j.ImportBatch).WithMany().HasForeignKey(j => j.ImportBatchId);
            justification.HasOne(j => j.User).WithMany().HasForeignKey(j => j.UserId);
        });

        modelBuilder.Entity<ChangeSet>(set =>
        {
            set.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            set.HasOne(s => s.Justification).WithMany().HasForeignKey(s => s.JustificationId);
            set.HasOne(s => s.Cause).WithMany().HasForeignKey(s => s.CauseChangeSetId);
        });

        modelBuilder.Entity<Change>(change =>
        {
            change.HasOne(c => c.ChangeSet).WithMany().HasForeignKey(c => c.ChangeSetId);

            // A record's history is the changes to it and the links on it, newest first.
            change.HasIndex(c => new { c.EntityKind, c.EntityId, c.Id });
            change.HasIndex(c => new { c.RelatedId, c.Id });
            change.HasIndex(c => c.ChangeSetId);
        });

        modelBuilder.Entity<GraphNodeRow>(node =>
        {
            node.HasNoKey().ToView("graph_nodes");
            node.Property(n => n.Props).HasColumnType("jsonb");
        });

        modelBuilder.Entity<GraphEdgeRow>(edge =>
        {
            edge.HasNoKey().ToView("graph_edges");
            edge.Property(e => e.Props).HasColumnType("jsonb");
        });

        ApplyDatabaseConventions(modelBuilder);
    }

    // Deletes never cascade (removing a crag must not silently remove ascents), timestamps default
    // to now() for raw SQL inserts, and every database object name is snake_case.
    private static void ApplyDatabaseConventions(ModelBuilder modelBuilder)
    {
        // Tables first: the default constraint names below are built from the principal table's name.
        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(e => e.GetViewName() is null))
        {
            entity.SetTableName(Snake(entity.ClrType.Name));
        }

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(Snake(property.Name));
            }

            if (typeof(ITimestamped).IsAssignableFrom(entity.ClrType))
            {
                entity.FindProperty(nameof(ITimestamped.CreatedAt))!.SetDefaultValueSql("now()");
                entity.FindProperty(nameof(ITimestamped.UpdatedAt))!.SetDefaultValueSql("now()");
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(Snake(key.GetName()!));
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
                foreignKey.SetConstraintName(Snake(foreignKey.GetConstraintName()!));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(Snake(index.GetDatabaseName()!));
            }
        }
    }

    private static string Snake(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);

    // CreatedAt and UpdatedAt are identical on insert, so "has this row been edited since it was
    // imported?" (used by import undo) is simply UpdatedAt != CreatedAt.
    private void StampTimestamps()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<ITimestamped>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
