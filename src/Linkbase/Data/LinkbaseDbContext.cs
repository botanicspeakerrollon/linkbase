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
    public DbSet<Area> Areas => Set<Area>();
    public DbSet<Crag> Crags => Set<Crag>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<Ascent> Ascents => Set<Ascent>();
    public DbSet<ExternalRef> ExternalRefs => Set<ExternalRef>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<AscentPartner> AscentPartners => Set<AscentPartner>();
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<GraphScene> GraphScenes => Set<GraphScene>();
    public DbSet<NodeType> NodeTypes => Set<NodeType>();
    public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();
    public DbSet<LinkDefinition> LinkDefinitions => Set<LinkDefinition>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<NodeLink> NodeLinks => Set<NodeLink>();
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
        configurationBuilder.Properties<ExternalEntityType>().HaveConversion<SnakeCaseEnumConverter<ExternalEntityType>>();
        configurationBuilder.Properties<ImportStatus>().HaveConversion<SnakeCaseEnumConverter<ImportStatus>>();
        configurationBuilder.Properties<DatePrecision>().HaveConversion<SnakeCaseEnumConverter<DatePrecision>>();
        configurationBuilder.Properties<FieldType>().HaveConversion<SnakeCaseEnumConverter<FieldType>>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<Area>(area =>
        {
            area.HasOne(a => a.Parent).WithMany(a => a.Children).HasForeignKey(a => a.ParentId);
            // Without NULLS NOT DISTINCT, Postgres would allow duplicate root areas.
            area.HasIndex(a => new { a.ParentId, a.Name }).IsUnique().AreNullsDistinct(false);
        });

        modelBuilder.Entity<Crag>(crag =>
        {
            crag.ToTable(t =>
            {
                t.HasCheckConstraint("ck_crag_lat", "lat BETWEEN -90 AND 90");
                t.HasCheckConstraint("ck_crag_lon", "lon BETWEEN -180 AND 180");
            });
            crag.Property(c => c.Aliases).HasColumnType("text[]").HasDefaultValueSql("'{}'");
            crag.HasIndex(c => c.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
        });

        modelBuilder.Entity<Route>(route =>
        {
            route.HasIndex(r => r.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
        });

        modelBuilder.Entity<Ascent>(ascent =>
        {
            ascent.ToTable(t => t.HasCheckConstraint("ck_ascent_my_rating", "my_rating BETWEEN 0 AND 3"));
            ascent.HasIndex(a => new { a.UserId, a.Date });
        });

        modelBuilder.Entity<ExternalRef>(externalRef =>
        {
            // entity_type is part of the key because a source's ids are only unique per kind of
            // thing: UKC crag 123 and UKC climb 123 are different records.
            externalRef.HasIndex(r => new { r.Source, r.EntityType, r.ExternalId }).IsUnique();
            externalRef.HasIndex(r => new { r.EntityType, r.EntityId });
            externalRef.HasIndex(r => new { r.Source, r.NodeType, r.ExternalId }).IsUnique();
            externalRef.HasIndex(r => r.NodeId);

            // No foreign key to node: a model reload truncates node, and that would have to truncate the climbing refs too.
            externalRef.ToTable(t => t.HasCheckConstraint("ck_external_ref_target",
                "(entity_type IS NOT NULL AND entity_id IS NOT NULL AND node_id IS NULL AND node_type IS NULL)"
                + " OR (entity_type IS NULL AND entity_id IS NULL AND node_id IS NOT NULL AND node_type IS NOT NULL)"));
        });

        modelBuilder.Entity<ImportBatch>(batch =>
        {
            batch.Property(b => b.RawPayload).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Partner>(partner =>
        {
            partner.HasIndex(p => new { p.UserId, p.Name }).IsUnique();
        });

        modelBuilder.Entity<AscentPartner>(link =>
        {
            link.HasKey(l => new { l.AscentId, l.PartnerId });
        });

        modelBuilder.Entity<Trip>(trip =>
        {
            trip.HasIndex(t => new { t.UserId, t.Key }).IsUnique();
        });

        modelBuilder.Entity<GraphScene>(scene =>
        {
            scene.Property(s => s.Nodes).HasColumnType("jsonb");
            scene.HasIndex(s => new { s.UserId, s.Name }).IsUnique();
        });

        modelBuilder.Entity<NodeType>(type => type.HasKey(t => t.Name));

        modelBuilder.Entity<FieldDefinition>(field =>
        {
            field.HasOne(f => f.NodeType).WithMany(t => t.Fields).HasForeignKey(f => f.NodeTypeName);
            field.HasIndex(f => new { f.NodeTypeName, f.Key }).IsUnique();
        });

        modelBuilder.Entity<LinkDefinition>(link =>
        {
            link.HasOne(l => l.Source).WithMany().HasForeignKey(l => l.SourceType);
            link.HasOne(l => l.Target).WithMany().HasForeignKey(l => l.TargetType);
            link.HasIndex(l => new { l.SourceType, l.RelType, l.TargetType }).IsUnique();
        });

        modelBuilder.Entity<Node>(node =>
        {
            node.HasOne(n => n.NodeType).WithMany().HasForeignKey(n => n.Type);
            node.Property(n => n.Props).HasColumnType("jsonb");
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
