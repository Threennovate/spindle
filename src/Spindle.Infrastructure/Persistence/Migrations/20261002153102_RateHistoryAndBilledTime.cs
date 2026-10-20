using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RateHistoryAndBilledTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SeparateBilledTime",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "BilledTimes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Seconds = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BilledTimes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BilledTimes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BilledTimes_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BilledTimes_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Rates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rates_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Rates_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Rates_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BilledTimes_ProjectId_UserId_Day",
                table: "BilledTimes",
                columns: new[] { "ProjectId", "UserId", "Day" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BilledTimes_UserId",
                table: "BilledTimes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_BilledTimes_WorkspaceId_Day",
                table: "BilledTimes",
                columns: new[] { "WorkspaceId", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_Rates_ProjectId",
                table: "Rates",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Rates_UserId",
                table: "Rates",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Rates_WorkspaceId_Kind_ProjectId_UserId_EffectiveFrom",
                table: "Rates",
                columns: new[] { "WorkspaceId", "Kind", "ProjectId", "UserId", "EffectiveFrom" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            // Every rate set so far becomes a period from the start, so reports add up as before. Rates of removed
            // members, projects and project members went with them, and stay gone.
            migrationBuilder.Sql("""
                INSERT INTO "Rates" ("Id", "WorkspaceId", "Kind", "ProjectId", "UserId", "EffectiveFrom", "Amount", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), "Id", 'Billable', NULL, NULL, NULL, "DefaultHourlyRate", now(), now()
                FROM "Workspaces" WHERE "DefaultHourlyRate" IS NOT NULL AND "DeletedAt" IS NULL;

                INSERT INTO "Rates" ("Id", "WorkspaceId", "Kind", "ProjectId", "UserId", "EffectiveFrom", "Amount", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), "WorkspaceId", 'Billable', NULL, "UserId", NULL, "HourlyRate", now(), now()
                FROM "WorkspaceMembers" WHERE "HourlyRate" IS NOT NULL AND "DeletedAt" IS NULL;

                INSERT INTO "Rates" ("Id", "WorkspaceId", "Kind", "ProjectId", "UserId", "EffectiveFrom", "Amount", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), "WorkspaceId", 'LaborCost', NULL, "UserId", NULL, "LaborCost", now(), now()
                FROM "WorkspaceMembers" WHERE "LaborCost" IS NOT NULL AND "DeletedAt" IS NULL;

                INSERT INTO "Rates" ("Id", "WorkspaceId", "Kind", "ProjectId", "UserId", "EffectiveFrom", "Amount", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), "WorkspaceId", 'Billable', "Id", NULL, NULL, "HourlyRate", now(), now()
                FROM "Projects" WHERE "HourlyRate" IS NOT NULL AND "DeletedAt" IS NULL;

                INSERT INTO "Rates" ("Id", "WorkspaceId", "Kind", "ProjectId", "UserId", "EffectiveFrom", "Amount", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), "WorkspaceId", 'Billable', "ProjectId", "UserId", NULL, "HourlyRate", now(), now()
                FROM "ProjectMembers" WHERE "HourlyRate" IS NOT NULL AND "DeletedAt" IS NULL;
                """);

            migrationBuilder.DropColumn(
                name: "DefaultHourlyRate",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "HourlyRate",
                table: "WorkspaceMembers");

            migrationBuilder.DropColumn(
                name: "LaborCost",
                table: "WorkspaceMembers");

            migrationBuilder.DropColumn(
                name: "HourlyRate",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "HourlyRate",
                table: "ProjectMembers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BilledTimes");

            migrationBuilder.DropColumn(
                name: "SeparateBilledTime",
                table: "Projects");

            migrationBuilder.AddColumn<decimal>(
                name: "DefaultHourlyRate",
                table: "Workspaces",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HourlyRate",
                table: "WorkspaceMembers",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LaborCost",
                table: "WorkspaceMembers",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HourlyRate",
                table: "Projects",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HourlyRate",
                table: "ProjectMembers",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            // Each rate goes back to what it was last set to; its history is lost.
            const string current = """
                SELECT DISTINCT ON ("WorkspaceId", "Kind", "ProjectId", "UserId") "WorkspaceId", "Kind", "ProjectId", "UserId", "Amount"
                FROM "Rates" WHERE "DeletedAt" IS NULL
                ORDER BY "WorkspaceId", "Kind", "ProjectId", "UserId", "EffectiveFrom" DESC NULLS LAST
                """;
            migrationBuilder.Sql($"""
                UPDATE "Workspaces" w SET "DefaultHourlyRate" = r."Amount" FROM ({current}) r
                WHERE r."WorkspaceId" = w."Id" AND r."Kind" = 'Billable' AND r."ProjectId" IS NULL AND r."UserId" IS NULL;
                """);
            migrationBuilder.Sql($"""
                UPDATE "WorkspaceMembers" m SET "HourlyRate" = r."Amount" FROM ({current}) r
                WHERE r."WorkspaceId" = m."WorkspaceId" AND r."Kind" = 'Billable' AND r."ProjectId" IS NULL AND r."UserId" = m."UserId";
                """);
            migrationBuilder.Sql($"""
                UPDATE "WorkspaceMembers" m SET "LaborCost" = r."Amount" FROM ({current}) r
                WHERE r."WorkspaceId" = m."WorkspaceId" AND r."Kind" = 'LaborCost' AND r."UserId" = m."UserId";
                """);
            migrationBuilder.Sql($"""
                UPDATE "Projects" p SET "HourlyRate" = r."Amount" FROM ({current}) r
                WHERE r."ProjectId" = p."Id" AND r."Kind" = 'Billable' AND r."UserId" IS NULL;
                """);
            migrationBuilder.Sql($"""
                UPDATE "ProjectMembers" m SET "HourlyRate" = r."Amount" FROM ({current}) r
                WHERE r."ProjectId" = m."ProjectId" AND r."Kind" = 'Billable' AND r."UserId" = m."UserId";
                """);

            migrationBuilder.DropTable(
                name: "Rates");
        }
    }
}
