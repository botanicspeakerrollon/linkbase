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

            // The generated search_text column on node needs this, so it has to exist before the table.
            migrationBuilder.Sql("""
CREATE FUNCTION graph_search_text(name text, props jsonb) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$
    SELECT coalesce(name, '') || ' ' || coalesce((SELECT string_agg(v, ' ') FROM jsonb_each_text(props) kv(k, v)), '')
$$;
""");

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
                name: "data_protection_key",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_key", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "model_setting",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    record_history = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    require_reason = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    source_kinds = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    security = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_setting", x => x.id);
                    table.CheckConstraint("ck_model_setting_one_row", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "node_property_label",
                columns: table => new
                {
                    node_id = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    label_level = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    label_any = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{*}'"),
                    label_all = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_property_label", x => new { x.node_id, x.key });
                });

            migrationBuilder.CreateTable(
                name: "node_type",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    color = table.Column<string>(type: "text", nullable: true),
                    icon = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    owner = table.Column<string>(type: "text", nullable: true),
                    hierarchy_field = table.Column<string>(type: "text", nullable: true),
                    hierarchy_rel = table.Column<string>(type: "text", nullable: true),
                    name_template = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_type", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "user_model",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    database = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_model", x => x.key);
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
                name: "saved_view",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    pinned = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_view", x => x.id);
                    table.ForeignKey(
                        name: "fk_saved_view_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_definition",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    node_type_name = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    options = table.Column<List<string>>(type: "text[]", nullable: true),
                    required = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    owner = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    group_name = table.Column<string>(type: "text", nullable: true),
                    multiple = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    label_level = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    label_any = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{*}'"),
                    label_all = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_field_definition", x => x.id);
                    table.ForeignKey(
                        name: "fk_field_definition_node_type_node_type_name",
                        column: x => x.node_type_name,
                        principalTable: "node_type",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hierarchy_level",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    node_type_name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    is_root = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    under = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hierarchy_level", x => x.id);
                    table.ForeignKey(
                        name: "fk_hierarchy_level_node_type_node_type_name",
                        column: x => x.node_type_name,
                        principalTable: "node_type",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "link_definition",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_type = table.Column<string>(type: "text", nullable: false),
                    rel_type = table.Column<string>(type: "text", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    max_links = table.Column<int>(type: "integer", nullable: true),
                    links = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_link_definition", x => x.id);
                    table.ForeignKey(
                        name: "fk_link_definition_node_type_source_type",
                        column: x => x.source_type,
                        principalTable: "node_type",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_link_definition_node_type_target_type",
                        column: x => x.target_type,
                        principalTable: "node_type",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "external_ref",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    node_id = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    node_type = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
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
                name: "justification",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    source_id = table.Column<string>(type: "text", nullable: true),
                    source_kind = table.Column<string>(type: "text", nullable: true),
                    source_comment = table.Column<string>(type: "text", nullable: true),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_justification", x => x.id);
                    table.ForeignKey(
                        name: "fk_justification_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_justification_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "node",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    props = table.Column<string>(type: "jsonb", nullable: false),
                    search_text = table.Column<string>(type: "text", nullable: true, computedColumnSql: "graph_search_text(name, props)", stored: true),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    label_level = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    label_any = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{*}'"),
                    label_all = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node", x => x.id);
                    table.ForeignKey(
                        name: "fk_node_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_node_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_node_node_type_type",
                        column: x => x.type,
                        principalTable: "node_type",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "staged_item",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    props = table.Column<string>(type: "jsonb", nullable: true),
                    rel_type = table.Column<string>(type: "text", nullable: true),
                    source_id = table.Column<string>(type: "text", nullable: true),
                    target_id = table.Column<string>(type: "text", nullable: true),
                    target_type = table.Column<string>(type: "text", nullable: true),
                    included = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staged_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_staged_item_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "link_attribute",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    link_definition_id = table.Column<long>(type: "bigint", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    options = table.Column<List<string>>(type: "text[]", nullable: true),
                    required = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    multiple = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_link_attribute", x => x.id);
                    table.ForeignKey(
                        name: "fk_link_attribute_link_definition_link_definition_id",
                        column: x => x.link_definition_id,
                        principalTable: "link_definition",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "change_set",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    justification_id = table.Column<long>(type: "bigint", nullable: false),
                    cause_change_set_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_set", x => x.id);
                    table.ForeignKey(
                        name: "fk_change_set_change_set_cause_change_set_id",
                        column: x => x.cause_change_set_id,
                        principalTable: "change_set",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_change_set_justification_justification_id",
                        column: x => x.justification_id,
                        principalTable: "justification",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "node_link",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    rel_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<string>(type: "text", nullable: false),
                    props = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    label_level = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    label_any = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{*}'"),
                    label_all = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_link", x => x.id);
                    table.ForeignKey(
                        name: "fk_node_link_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_node_link_node_source_id",
                        column: x => x.source_id,
                        principalTable: "node",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_node_link_node_target_id",
                        column: x => x.target_id,
                        principalTable: "node",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "change",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    change_set_id = table.Column<long>(type: "bigint", nullable: false),
                    entity_kind = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    op = table.Column<string>(type: "text", nullable: false),
                    field_key = table.Column<string>(type: "text", nullable: true),
                    rel_type = table.Column<string>(type: "text", nullable: true),
                    related_id = table.Column<string>(type: "text", nullable: true),
                    old_value = table.Column<string>(type: "text", nullable: true),
                    new_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change", x => x.id);
                    table.ForeignKey(
                        name: "fk_change_change_set_change_set_id",
                        column: x => x.change_set_id,
                        principalTable: "change_set",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_change_set_id",
                table: "change",
                column: "change_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_entity_kind_entity_id_id",
                table: "change",
                columns: new[] { "entity_kind", "entity_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_change_related_id_id",
                table: "change",
                columns: new[] { "related_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_change_set_cause_change_set_id",
                table: "change_set",
                column: "cause_change_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_set_justification_id",
                table: "change_set",
                column: "justification_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_ref_import_batch_id",
                table: "external_ref",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_ref_node_id",
                table: "external_ref",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_ref_source_node_type_external_id",
                table: "external_ref",
                columns: new[] { "source", "node_type", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_field_definition_node_type_name_key",
                table: "field_definition",
                columns: new[] { "node_type_name", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_hierarchy_level_node_type_name_kind",
                table: "hierarchy_level",
                columns: new[] { "node_type_name", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_batch_user_id",
                table: "import_batch",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_justification_import_batch_id",
                table: "justification",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_justification_user_id",
                table: "justification",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_link_attribute_link_definition_id_key",
                table: "link_attribute",
                columns: new[] { "link_definition_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_link_definition_source_type_rel_type_target_type",
                table: "link_definition",
                columns: new[] { "source_type", "rel_type", "target_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_link_definition_target_type",
                table: "link_definition",
                column: "target_type");

            migrationBuilder.CreateIndex(
                name: "ix_node_import_batch_id",
                table: "node",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_name",
                table: "node",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_node_search_text",
                table: "node",
                column: "search_text")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_node_type_key",
                table: "node",
                columns: new[] { "type", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_node_type_name_key",
                table: "node",
                columns: new[] { "type", "name", "key" });

            migrationBuilder.CreateIndex(
                name: "ix_node_user_id",
                table: "node",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_link_import_batch_id",
                table: "node_link",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_link_source_id",
                table: "node_link",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_link_target_id",
                table: "node_link",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_view_user_id_name",
                table: "saved_view",
                columns: new[] { "user_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_staged_item_import_batch_id_kind_id",
                table: "staged_item",
                columns: new[] { "import_batch_id", "kind", "id" });

            // The single user every row belongs to until sign-in exists.
            migrationBuilder.Sql("INSERT INTO app_user (display_name) VALUES ('Me');");

            // The graph views are SQL the model can't describe, so they are written out here.
            migrationBuilder.Sql("""
CREATE VIEW graph_edges AS
 SELECT source_id,
    target_id,
    rel_type,
    props
   FROM node_link;
CREATE VIEW graph_nodes AS
 SELECT id AS node_id,
    user_id,
    type AS label,
    name,
    props,
    search_text
   FROM node;
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS graph_nodes, graph_edges;");

            migrationBuilder.DropTable(
                name: "change");

            migrationBuilder.DropTable(
                name: "data_protection_key");

            migrationBuilder.DropTable(
                name: "external_ref");

            migrationBuilder.DropTable(
                name: "field_definition");

            migrationBuilder.DropTable(
                name: "hierarchy_level");

            migrationBuilder.DropTable(
                name: "link_attribute");

            migrationBuilder.DropTable(
                name: "model_setting");

            migrationBuilder.DropTable(
                name: "node_link");

            migrationBuilder.DropTable(
                name: "node_property_label");

            migrationBuilder.DropTable(
                name: "saved_view");

            migrationBuilder.DropTable(
                name: "staged_item");

            migrationBuilder.DropTable(
                name: "user_model");

            migrationBuilder.DropTable(
                name: "change_set");

            migrationBuilder.DropTable(
                name: "link_definition");

            migrationBuilder.DropTable(
                name: "node");

            migrationBuilder.DropTable(
                name: "justification");

            migrationBuilder.DropTable(
                name: "node_type");

            migrationBuilder.DropTable(
                name: "import_batch");

            migrationBuilder.DropTable(
                name: "app_user");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS graph_search_text(text, jsonb);");
        }
    }
}
