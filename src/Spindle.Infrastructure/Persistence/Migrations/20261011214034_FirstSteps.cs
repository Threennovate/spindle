using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FirstSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstStepsHiddenAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            // People who were here before first steps don't need them.
            migrationBuilder.Sql("""UPDATE "AspNetUsers" SET "FirstStepsHiddenAt" = now();""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstStepsHiddenAt",
                table: "AspNetUsers");
        }
    }
}
