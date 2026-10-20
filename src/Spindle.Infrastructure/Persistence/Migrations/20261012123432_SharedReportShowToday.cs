using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedReportShowToday : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowToday",
                table: "SharedReports",
                type: "boolean",
                nullable: false,
                // Links made before there was a choice keep showing today's time.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowToday",
                table: "SharedReports");
        }
    }
}
