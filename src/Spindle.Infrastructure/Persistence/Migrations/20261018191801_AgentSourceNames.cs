using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spindle.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Agents are named as the apps name them (<c>claude</c>), no longer by the names of a closed list (<c>Claude</c>),
    /// so a new agent needs no new server. The column stays as it is; only its values change.
    /// </summary>
    public partial class AgentSourceNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "AgentSessions" SET "Source" = lower("Source");""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Older servers know only these two.
            migrationBuilder.Sql("""DELETE FROM "AgentSessions" WHERE "Source" NOT IN ('claude', 'codex');""");
            migrationBuilder.Sql("""UPDATE "AgentSessions" SET "Source" = initcap("Source");""");
        }
    }
}
