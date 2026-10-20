using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PriceCohorts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PriceCohort",
                table: "OrganizationSubscriptions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            // Organizations from before price cohorts are the first ones, and keep the founding prices if they ever pay.
            migrationBuilder.Sql("""UPDATE "OrganizationSubscriptions" SET "PriceCohort" = 'founding';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PriceCohort",
                table: "OrganizationSubscriptions");
        }
    }
}
