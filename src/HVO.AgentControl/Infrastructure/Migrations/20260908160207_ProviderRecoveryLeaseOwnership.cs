using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProviderRecoveryLeaseOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecoveryCommandId",
                table: "ProviderPool",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "RecoveryOwnershipUnknown",
                table: "ProviderPool",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // The prior schema stored the admitted attempt in LastCommandId only
            // while Recovering. Do not guess an owner from unrelated failure history.
            migrationBuilder.Sql("""
                UPDATE ProviderPool
                SET RecoveryCommandId = LastCommandId
                WHERE State = 'Recovering' AND EXISTS (
                    SELECT 1 FROM Commands c JOIN Workers w ON w.Id = c.WorkerId
                    JOIN Runtimes r ON r.Id = c.RuntimeId
                    WHERE c.Id = ProviderPool.LastCommandId AND c.Kind = 'Prompt'
                      AND c.ProviderPoolId = ProviderPool.Id
                      AND ProviderPool.Id = 'provider:' || ProviderPool.ProviderId
                      AND w.RuntimeId = c.RuntimeId
                      AND c.State IN ('Queued', 'Dispatching', 'AcceptedByRuntime', 'Running', 'DeliveryUnknown')
                );
                UPDATE ProviderPool
                SET RecoveryOwnershipUnknown = 1, State = 'RecoveryRequired', RetryAt = NULL
                WHERE State = 'Recovering' AND RecoveryCommandId = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecoveryCommandId",
                table: "ProviderPool");

            migrationBuilder.DropColumn(
                name: "RecoveryOwnershipUnknown",
                table: "ProviderPool");
        }
    }
}
