using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "app_user",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    display_name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_user", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "import_batch",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    raw_payload = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_batch", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_batch_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "area",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    parent_id = table.Column<long>(type: "bigint", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true),
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_area", x => x.id);
                    table.ForeignKey(
                        name: "fk_area_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_area_area_parent_id",
                        column: x => x.parent_id,
                        principalTable: "area",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_area_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "external_ref",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_ref", x => x.id);
                    table.ForeignKey(
                        name: "fk_external_ref_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crag",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    area_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    rock_type = table.Column<string>(type: "text", nullable: true),
                    lat = table.Column<double>(type: "double precision", nullable: true),
                    lon = table.Column<double>(type: "double precision", nullable: true),
                    aliases = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true),
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crag", x => x.id);
                    table.CheckConstraint("ck_crag_lat", "lat BETWEEN -90 AND 90");
                    table.CheckConstraint("ck_crag_lon", "lon BETWEEN -180 AND 180");
                    table.ForeignKey(
                        name: "fk_crag_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crag_area_area_id",
                        column: x => x.area_id,
                        principalTable: "area",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crag_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "route",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    crag_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    grade = table.Column<string>(type: "text", nullable: true),
                    grade_system = table.Column<string>(type: "text", nullable: true),
                    discipline = table.Column<string>(type: "text", nullable: true),
                    length_m = table.Column<int>(type: "integer", nullable: true),
                    pitches = table.Column<int>(type: "integer", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true),
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_route", x => x.id);
                    table.ForeignKey(
                        name: "fk_route_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_route_crag_crag_id",
                        column: x => x.crag_id,
                        principalTable: "crag",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_route_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ascent",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    route_id = table.Column<long>(type: "bigint", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: true),
                    style = table.Column<string>(type: "text", nullable: false),
                    style_raw = table.Column<string>(type: "text", nullable: true),
                    partners = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    my_rating = table.Column<short>(type: "smallint", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true),
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ascent", x => x.id);
                    table.CheckConstraint("ck_ascent_my_rating", "my_rating BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "fk_ascent_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ascent_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ascent_route_route_id",
                        column: x => x.route_id,
                        principalTable: "route",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_area_import_batch_id",
                table: "area",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_area_parent_id_name",
                table: "area",
                columns: new[] { "parent_id", "name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_area_user_id",
                table: "area",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_ascent_import_batch_id",
                table: "ascent",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_ascent_route_id",
                table: "ascent",
                column: "route_id");

            migrationBuilder.CreateIndex(
                name: "ix_ascent_user_id_date",
                table: "ascent",
                columns: new[] { "user_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_crag_area_id",
                table: "crag",
                column: "area_id");

            migrationBuilder.CreateIndex(
                name: "ix_crag_import_batch_id",
                table: "crag",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_crag_name",
                table: "crag",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_crag_user_id",
                table: "crag",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_ref_entity_type_entity_id",
                table: "external_ref",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_external_ref_import_batch_id",
                table: "external_ref",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_ref_source_entity_type_external_id",
                table: "external_ref",
                columns: new[] { "source", "entity_type", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_batch_user_id",
                table: "import_batch",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_route_crag_id",
                table: "route",
                column: "crag_id");

            migrationBuilder.CreateIndex(
                name: "ix_route_import_batch_id",
                table: "route",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_route_name",
                table: "route",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_route_user_id",
                table: "route",
                column: "user_id");

            // The single user that every owned row references.
            migrationBuilder.Sql("INSERT INTO app_user (display_name) VALUES ('Me');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ascent");

            migrationBuilder.DropTable(
                name: "external_ref");

            migrationBuilder.DropTable(
                name: "route");

            migrationBuilder.DropTable(
                name: "crag");

            migrationBuilder.DropTable(
                name: "area");

            migrationBuilder.DropTable(
                name: "import_batch");

            migrationBuilder.DropTable(
                name: "app_user");
        }
    }
}
