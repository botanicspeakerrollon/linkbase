using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartnersTripsAndReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "partners",
                table: "ascent");

            migrationBuilder.AlterColumn<DateOnly>(
                name: "date",
                table: "ascent",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1),
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "date_precision",
                table: "ascent",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "trip_id",
                table: "ascent",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "trip_inferred",
                table: "ascent",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "partner",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true),
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_partner", x => x.id);
                    table.ForeignKey(
                        name: "fk_partner_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_partner_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_item",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<long>(type: "bigint", nullable: false),
                    candidate_id = table.Column<long>(type: "bigint", nullable: false),
                    similarity = table.Column<double>(type: "double precision", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_review_item_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "trip",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    sort_date = table.Column<DateOnly>(type: "date", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    abroad = table.Column<bool>(type: "boolean", nullable: false),
                    home = table.Column<bool>(type: "boolean", nullable: false),
                    approximate = table.Column<bool>(type: "boolean", nullable: false),
                    dates_partly_unknown = table.Column<bool>(type: "boolean", nullable: false),
                    manual = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true),
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trip", x => x.id);
                    table.ForeignKey(
                        name: "fk_trip_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_trip_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ascent_partner",
                columns: table => new
                {
                    ascent_id = table.Column<long>(type: "bigint", nullable: false),
                    partner_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ascent_partner", x => new { x.ascent_id, x.partner_id });
                    table.ForeignKey(
                        name: "fk_ascent_partner_ascent_ascent_id",
                        column: x => x.ascent_id,
                        principalTable: "ascent",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ascent_partner_partner_partner_id",
                        column: x => x.partner_id,
                        principalTable: "partner",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ascent_trip_id",
                table: "ascent",
                column: "trip_id");

            migrationBuilder.CreateIndex(
                name: "ix_ascent_partner_partner_id",
                table: "ascent_partner",
                column: "partner_id");

            migrationBuilder.CreateIndex(
                name: "ix_partner_import_batch_id",
                table: "partner",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_partner_user_id_name",
                table: "partner",
                columns: new[] { "user_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_item_import_batch_id",
                table: "review_item",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_item_status",
                table: "review_item",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_trip_import_batch_id",
                table: "trip",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_trip_user_id_key",
                table: "trip",
                columns: new[] { "user_id", "key" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_ascent_trip_trip_id",
                table: "ascent",
                column: "trip_id",
                principalTable: "trip",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_ascent_trip_trip_id",
                table: "ascent");

            migrationBuilder.DropTable(
                name: "ascent_partner");

            migrationBuilder.DropTable(
                name: "review_item");

            migrationBuilder.DropTable(
                name: "trip");

            migrationBuilder.DropTable(
                name: "partner");

            migrationBuilder.DropIndex(
                name: "ix_ascent_trip_id",
                table: "ascent");

            migrationBuilder.DropColumn(
                name: "date_precision",
                table: "ascent");

            migrationBuilder.DropColumn(
                name: "trip_id",
                table: "ascent");

            migrationBuilder.DropColumn(
                name: "trip_inferred",
                table: "ascent");

            migrationBuilder.AlterColumn<DateOnly>(
                name: "date",
                table: "ascent",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateOnly),
                oldType: "date");

            migrationBuilder.AddColumn<string>(
                name: "partners",
                table: "ascent",
                type: "text",
                nullable: true);
        }
    }
}
