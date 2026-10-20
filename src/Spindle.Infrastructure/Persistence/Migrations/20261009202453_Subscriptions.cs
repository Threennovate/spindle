using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Subscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrganizationSubscriptions",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TrialEndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PaidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationSubscriptions", x => x.OrganizationId);
                    table.ForeignKey(
                        name: "FK_OrganizationSubscriptions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Organizations from before the hosted service had no trial, and don't start one now.
            migrationBuilder.Sql("""INSERT INTO "OrganizationSubscriptions" ("OrganizationId", "Status") SELECT "Id", 'Comped' FROM "Organizations";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizationSubscriptions");
        }
    }
}
