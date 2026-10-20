using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "Seconds",
                table: "BilledTimes",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "BilledTimes",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SharedReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Token = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    Period = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: true),
                    To = table.Column<DateOnly>(type: "date", nullable: true),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WeekStart = table.Column<int>(type: "integer", nullable: false),
                    UserIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    GroupIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    ProjectIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    ClientIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    TaskIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    TagIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Rounding = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RoundingMinutes = table.Column<int>(type: "integer", nullable: false),
                    ShowPeople = table.Column<bool>(type: "boolean", nullable: false),
                    ShowAmounts = table.Column<bool>(type: "boolean", nullable: false),
                    HourFormat = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastOpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedReports_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SharedReports_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SharedReports_CreatedByUserId",
                table: "SharedReports",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedReports_Token",
                table: "SharedReports",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SharedReports_WorkspaceId",
                table: "SharedReports",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SharedReports");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "BilledTimes");

            migrationBuilder.AlterColumn<long>(
                name: "Seconds",
                table: "BilledTimes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }
    }
}
