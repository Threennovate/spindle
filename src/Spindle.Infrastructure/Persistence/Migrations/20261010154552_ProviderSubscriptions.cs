using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProviderSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BilledSeats",
                table: "OrganizationSubscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CancelsAtPeriodEnd",
                table: "OrganizationSubscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Interval",
                table: "OrganizationSubscriptions",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextChargeAt",
                table: "OrganizationSubscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "OrganizationSubscriptions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderCustomerId",
                table: "OrganizationSubscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderStatus",
                table: "OrganizationSubscriptions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderSubscriptionId",
                table: "OrganizationSubscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProviderUpdatedAt",
                table: "OrganizationSubscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProviderEventReceipts",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EventId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderEventReceipts", x => new { x.Provider, x.EventId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationSubscriptions_ProviderSubscriptionId",
                table: "OrganizationSubscriptions",
                column: "ProviderSubscriptionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderEventReceipts");

            migrationBuilder.DropIndex(
                name: "IX_OrganizationSubscriptions_ProviderSubscriptionId",
                table: "OrganizationSubscriptions");

            migrationBuilder.DropColumn(
                name: "BilledSeats",
                table: "OrganizationSubscriptions");

            migrationBuilder.DropColumn(
                name: "CancelsAtPeriodEnd",
                table: "OrganizationSubscriptions");

            migrationBuilder.DropColumn(
                name: "Interval",
                table: "OrganizationSubscriptions");

            migrationBuilder.DropColumn(
                name: "NextChargeAt",
                table: "OrganizationSubscriptions");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "OrganizationSubscriptions");

            migrationBuilder.DropColumn(
                name: "ProviderCustomerId",
                table: "OrganizationSubscriptions");

            migrationBuilder.DropColumn(
                name: "ProviderStatus",
                table: "OrganizationSubscriptions");

            migrationBuilder.DropColumn(
                name: "ProviderSubscriptionId",
                table: "OrganizationSubscriptions");

            migrationBuilder.DropColumn(
                name: "ProviderUpdatedAt",
                table: "OrganizationSubscriptions");
        }
    }
}
