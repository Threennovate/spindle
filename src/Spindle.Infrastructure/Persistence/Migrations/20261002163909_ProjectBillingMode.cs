using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectBillingMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillingMode",
                table: "Projects",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "AsTracked");

            // Projects with billed time entered separately keep it, day by day.
            migrationBuilder.Sql("""UPDATE "Projects" SET "BillingMode" = 'PerDay' WHERE "SeparateBilledTime";""");

            migrationBuilder.DropColumn(
                name: "SeparateBilledTime",
                table: "Projects");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SeparateBilledTime",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // A full-day project goes back to billed time entered day by day. Earlier code knows no daily hours.
            migrationBuilder.Sql("""UPDATE "Projects" SET "SeparateBilledTime" = "BillingMode" <> 'AsTracked';""");
            migrationBuilder.Sql("""DELETE FROM "Rates" WHERE "Kind" = 'DailyHours';""");

            migrationBuilder.DropColumn(
                name: "BillingMode",
                table: "Projects");
        }
    }
}
