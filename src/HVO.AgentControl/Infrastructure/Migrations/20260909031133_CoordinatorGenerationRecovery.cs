using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CoordinatorGenerationRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ControlSessions_PredecessorId",
                table: "ControlSessions");

            migrationBuilder.AddColumn<string>(
                name: "CommandId",
                table: "ModelUsage",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ControlSessionGeneration",
                table: "ModelUsage",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ControlSessionId",
                table: "ModelUsage",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoordinationRunId",
                table: "ModelUsage",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentNativeMessageId",
                table: "ModelUsage",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryIntentId",
                table: "ModelUsage",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OwnerPolicyRevision",
                table: "CoordinationRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "CreatedAt",
                table: "ControlSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "CreationAuthorizedAt",
                table: "ControlSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenerationReason",
                table: "ControlSessions",
                type: "TEXT",
                nullable: false,
                defaultValue: "Initial");

            migrationBuilder.AddColumn<string>(
                name: "RecoveryInstructionHash",
                table: "ControlSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryIntentId",
                table: "ControlSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RecoveryOwnerPolicyRevision",
                table: "ControlSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryRunId",
                table: "ControlSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoverySourceCommandId",
                table: "ControlSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplacementDecisionCommandId",
                table: "ControlSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CoordinatorNativeObservations",
                columns: table => new
                {
                    CommandId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerId = table.Column<string>(type: "TEXT", nullable: false),
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    NativeSessionId = table.Column<string>(type: "TEXT", nullable: false),
                    NativeCallerId = table.Column<string>(type: "TEXT", nullable: false),
                    ChildSessionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoordinatorNativeObservations", x => x.CommandId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModelUsage_CoordinationRunId_ControlSessionGeneration_CreatedAt",
                table: "ModelUsage",
                columns: new[] { "CoordinationRunId", "ControlSessionGeneration", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ControlSessions_PredecessorId",
                table: "ControlSessions",
                column: "PredecessorId",
                filter: "PredecessorId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ControlSessions_RecoveryIntentId",
                table: "ControlSessions",
                column: "RecoveryIntentId",
                unique: true,
                filter: "RecoveryIntentId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorNativeObservations_WorkerId_ObservedAt",
                table: "CoordinatorNativeObservations",
                columns: new[] { "WorkerId", "ObservedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoordinatorNativeObservations");

            migrationBuilder.DropIndex(
                name: "IX_ModelUsage_CoordinationRunId_ControlSessionGeneration_CreatedAt",
                table: "ModelUsage");

            migrationBuilder.DropIndex(
                name: "IX_ControlSessions_PredecessorId",
                table: "ControlSessions");

            migrationBuilder.DropIndex(
                name: "IX_ControlSessions_RecoveryIntentId",
                table: "ControlSessions");

            migrationBuilder.DropColumn(
                name: "CommandId",
                table: "ModelUsage");

            migrationBuilder.DropColumn(
                name: "ControlSessionGeneration",
                table: "ModelUsage");

            migrationBuilder.DropColumn(
                name: "ControlSessionId",
                table: "ModelUsage");

            migrationBuilder.DropColumn(
                name: "CoordinationRunId",
                table: "ModelUsage");

            migrationBuilder.DropColumn(
                name: "ParentNativeMessageId",
                table: "ModelUsage");

            migrationBuilder.DropColumn(
                name: "RecoveryIntentId",
                table: "ModelUsage");

            migrationBuilder.DropColumn(
                name: "OwnerPolicyRevision",
                table: "CoordinationRuns");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "ControlSessions");

            migrationBuilder.DropColumn(
                name: "CreationAuthorizedAt",
                table: "ControlSessions");

            migrationBuilder.DropColumn(
                name: "GenerationReason",
                table: "ControlSessions");

            migrationBuilder.DropColumn(
                name: "RecoveryInstructionHash",
                table: "ControlSessions");

            migrationBuilder.DropColumn(
                name: "RecoveryIntentId",
                table: "ControlSessions");

            migrationBuilder.DropColumn(
                name: "RecoveryOwnerPolicyRevision",
                table: "ControlSessions");

            migrationBuilder.DropColumn(
                name: "RecoveryRunId",
                table: "ControlSessions");

            migrationBuilder.DropColumn(
                name: "RecoverySourceCommandId",
                table: "ControlSessions");

            migrationBuilder.DropColumn(
                name: "ReplacementDecisionCommandId",
                table: "ControlSessions");

            migrationBuilder.CreateIndex(
                name: "IX_ControlSessions_PredecessorId",
                table: "ControlSessions",
                column: "PredecessorId",
                filter: "PredecessorId IS NOT NULL");
        }
    }
}
