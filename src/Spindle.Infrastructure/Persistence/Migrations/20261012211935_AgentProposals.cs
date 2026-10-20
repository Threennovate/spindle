using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Original = table.Column<string>(type: "jsonb", nullable: true),
                    Fields = table.Column<string>(type: "jsonb", nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Client = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProposedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResultEntryId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentProposals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentProposals_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentProposals_UserId_WorkspaceId_ProposedAt",
                table: "AgentProposals",
                columns: new[] { "UserId", "WorkspaceId", "ProposedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentProposals_WorkspaceId",
                table: "AgentProposals",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentProposals");
        }
    }
}
