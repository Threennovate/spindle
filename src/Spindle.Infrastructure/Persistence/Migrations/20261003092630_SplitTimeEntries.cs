using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SplitTimeEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSplit",
                table: "TimeEntries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Shares",
                table: "TimeEntries",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TimeEntries_SplitHasShares",
                table: "TimeEntries",
                sql: "\"IsSplit\" = (jsonb_array_length(\"Shares\") > 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TimeEntries_SplitHasShares",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "IsSplit",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "Shares",
                table: "TimeEntries");
        }
    }
}
