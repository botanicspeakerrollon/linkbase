using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropClimbingViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These were one domain's SQL in every model's database, and as views they ran without the viewer's row policies.
            // Insights now carries the same shapes as a WITH clause (ClimbingInsightsSql).
            migrationBuilder.Sql("DROP VIEW IF EXISTS climbing_trip, climbing_partner, climbing_ascent_partner, climbing_ascent_facts, climbing_crag_country");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: no code reads the views. The definitions are in ClimbingInsightsSql and in InitialCreate.
        }
    }
}
