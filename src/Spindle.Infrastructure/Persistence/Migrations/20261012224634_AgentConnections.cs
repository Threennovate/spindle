using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentConnections",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Scope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Resource = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentConnections", x => x.SessionId);
                    table.ForeignKey(
                        name: "FK_AgentConnections_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentConnections_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OAuthAuthorizationCodes",
                columns: table => new
                {
                    CodeHash = table.Column<byte[]>(type: "bytea", fixedLength: true, maxLength: 32, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ClientName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RedirectUri = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CodeChallenge = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Scope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Resource = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthAuthorizationCodes", x => x.CodeHash);
                    table.ForeignKey(
                        name: "FK_OAuthAuthorizationCodes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OAuthAuthorizationCodes_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OAuthClients",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(43)", maxLength: 43, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RedirectUris = table.Column<string[]>(type: "text[]", nullable: false),
                    SecretHash = table.Column<byte[]>(type: "bytea", fixedLength: true, maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthClients", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentConnections_UserId",
                table: "AgentConnections",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentConnections_WorkspaceId",
                table: "AgentConnections",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthAuthorizationCodes_ExpiresAt",
                table: "OAuthAuthorizationCodes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthAuthorizationCodes_UserId",
                table: "OAuthAuthorizationCodes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthAuthorizationCodes_WorkspaceId",
                table: "OAuthAuthorizationCodes",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentConnections");

            migrationBuilder.DropTable(
                name: "OAuthAuthorizationCodes");

            migrationBuilder.DropTable(
                name: "OAuthClients");
        }
    }
}
