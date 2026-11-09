using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class SwitchClimbingToGenericStore : Migration
    {
        // Dropping the old tables loses a logbook that was never copied, so this refuses to run until it has been. The generic
        // store needs at least as many records of each type as the old table has rows, so a stale or half-finished copy stops it
        // too, and at least as many external ids as the old ones. Trips are the exception: they are derived and rebuilt from the
        // ascents, so only that some exist is checked. Empty databases, such as a model that never held climbing, pass.
        private const string Guard = """
            DO $$
            DECLARE
                pair record;
                typed bigint;
                generic bigint;
            BEGIN
                FOR pair IN SELECT * FROM (VALUES
                    ('area', 'Area'), ('crag', 'Crag'), ('route', 'Route'), ('ascent', 'Ascent'), ('partner', 'Partner'), ('trip', 'Trip')
                ) AS t(tbl, node_type)
                LOOP
                    EXECUTE format('SELECT count(*) FROM %I', pair.tbl) INTO typed;
                    SELECT count(*) INTO generic FROM node WHERE type = pair.node_type;
                    IF typed > 0 AND (generic = 0 OR (generic < typed AND pair.node_type <> 'Trip')) THEN
                        RAISE EXCEPTION 'The % table holds % rows and the generic store has % % records. Copy the logbook first: climbing to-generic <model> --same-database (docs/climbing-switch.md).', pair.tbl, typed, generic, pair.node_type;
                    END IF;
                END LOOP;

                SELECT count(*) INTO typed FROM external_ref WHERE node_id IS NULL;
                SELECT count(*) INTO generic FROM external_ref WHERE node_id IS NOT NULL;
                IF typed > generic THEN
                    RAISE EXCEPTION 'There are % external ids for the old tables and only % for the generic store. Copy the logbook first (docs/climbing-switch.md).', typed, generic;
                END IF;
            END
            $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Guard);

            // The graph views stop reading the typed tables. The generic store was already part of them, so what Explore
            // shows is now only the generic store's records.
            migrationBuilder.Sql("""
                DROP VIEW graph_edges;
                DROP VIEW graph_nodes;
                DROP VIEW graph_edges_climbing;
                DROP VIEW graph_nodes_climbing;
                DROP VIEW ascent_facts;
                DROP VIEW crag_country;

                CREATE VIEW graph_nodes AS
                SELECT id AS node_id, user_id, type AS label, name, props, search_text FROM node;

                CREATE VIEW graph_edges AS
                SELECT source_id, target_id, rel_type, props FROM node_link;
                """);

            // Ids in the old tables' terms mean nothing without the tables.
            migrationBuilder.Sql("DELETE FROM external_ref WHERE node_id IS NULL;");

            migrationBuilder.DropTable(
                name: "ascent_partner");

            migrationBuilder.DropTable(
                name: "ascent");

            migrationBuilder.DropTable(
                name: "partner");

            migrationBuilder.DropTable(
                name: "route");

            migrationBuilder.DropTable(
                name: "trip");

            migrationBuilder.DropTable(
                name: "crag");

            migrationBuilder.DropTable(
                name: "area");

            migrationBuilder.DropIndex(
                name: "ix_external_ref_entity_type_entity_id",
                table: "external_ref");

            migrationBuilder.DropIndex(
                name: "ix_external_ref_source_entity_type_external_id",
                table: "external_ref");

            migrationBuilder.DropCheckConstraint(
                name: "ck_external_ref_target",
                table: "external_ref");

            migrationBuilder.DropColumn(
                name: "entity_id",
                table: "external_ref");

            migrationBuilder.DropColumn(
                name: "entity_type",
                table: "external_ref");

            migrationBuilder.AlterColumn<string>(
                name: "node_type",
                table: "external_ref",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "node_id",
                table: "external_ref",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "This migration drops the old climbing tables and can't be reversed. Restore the backup taken before it (docs/operations.md).");
        }
    }
}
