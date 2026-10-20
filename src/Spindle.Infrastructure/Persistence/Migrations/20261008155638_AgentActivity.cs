using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AgentActivitySync",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AgentActivitySyncedSince",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AgentActivityTitles",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AgentRepositoryLinks",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Identity = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRepositoryLinks", x => new { x.UserId, x.Identity });
                    table.ForeignKey(
                        name: "FK_AgentRepositoryLinks_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentRepositoryLinks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ParentSessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RepositoryIdentity = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RepositoryName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Branches = table.Column<string[]>(type: "text[]", nullable: false),
                    Touches = table.Column<long[]>(type: "bigint[]", nullable: false),
                    RunStarts = table.Column<long[]>(type: "bigint[]", nullable: false),
                    RunEnds = table.Column<long[]>(type: "bigint[]", nullable: false),
                    FirstAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ParserVersion = table.Column<int>(type: "integer", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentSessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SplitSuggestionDismissals",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SplitSuggestionDismissals", x => new { x.UserId, x.EntryId });
                    table.ForeignKey(
                        name: "FK_SplitSuggestionDismissals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SplitSuggestionDismissals_TimeEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "TimeEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRepositoryLinks_ProjectId",
                table: "AgentRepositoryLinks",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_UserId_DeviceId_Source_SessionId",
                table: "AgentSessions",
                columns: new[] { "UserId", "DeviceId", "Source", "SessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_UserId_FirstAt_LastAt",
                table: "AgentSessions",
                columns: new[] { "UserId", "FirstAt", "LastAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SplitSuggestionDismissals_EntryId",
                table: "SplitSuggestionDismissals",
                column: "EntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentRepositoryLinks");

            migrationBuilder.DropTable(
                name: "AgentSessions");

            migrationBuilder.DropTable(
                name: "SplitSuggestionDismissals");

            migrationBuilder.DropColumn(
                name: "AgentActivitySync",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AgentActivitySyncedSince",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AgentActivityTitles",
                table: "AspNetUsers");
        }
    }
}
