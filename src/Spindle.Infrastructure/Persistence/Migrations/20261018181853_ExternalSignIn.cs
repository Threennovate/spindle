using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExternalSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalSignInCodes",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    BindingHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NewAccount = table.Column<bool>(type: "boolean", nullable: false),
                    ReturnPath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalSignInCodes", x => x.TokenHash);
                    table.ForeignKey(
                        name: "FK_ExternalSignInCodes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalSignInCodes_ExpiresAt",
                table: "ExternalSignInCodes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalSignInCodes_UserId",
                table: "ExternalSignInCodes",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalSignInCodes");
        }
    }
}
