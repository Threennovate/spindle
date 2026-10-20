using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedReportSender : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Sender",
                table: "SharedReports",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // Links made before there was a choice keep showing the workspace's name.
                defaultValue: "Workspace");

            migrationBuilder.AddColumn<string>(
                name: "SenderName",
                table: "SharedReports",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Sender",
                table: "SharedReports");

            migrationBuilder.DropColumn(
                name: "SenderName",
                table: "SharedReports");
        }
    }
}
