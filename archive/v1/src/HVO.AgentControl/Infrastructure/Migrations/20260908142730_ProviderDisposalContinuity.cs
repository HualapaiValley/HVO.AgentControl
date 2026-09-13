using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProviderDisposalContinuity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingDisposalJson",
                table: "ProviderReadinessReceipt",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
            // Previous versions did not retain a process-attributed pending attempt.
            // Empty new fields must not authorize retry of an uncertain old disposal.
            migrationBuilder.Sql("UPDATE ProviderReadinessReceipt SET PendingDisposalJson = 'legacy-unattributed' WHERE ProviderId = 'instance' AND State IN ('Unknown', 'Refreshing');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingDisposalJson",
                table: "ProviderReadinessReceipt");
        }
    }
}
