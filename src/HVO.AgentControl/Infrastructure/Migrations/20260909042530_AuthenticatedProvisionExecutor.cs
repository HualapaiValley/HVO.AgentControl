using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AuthenticatedProvisionExecutor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ClaimGeneration",
                table: "ProvisionOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LastAppliedResultSequence",
                table: "ProvisionOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "SelectedAuthorityGeneration",
                table: "ProvisionOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SelectedEnrollmentId",
                table: "ProvisionOperations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SelectedIncarnationId",
                table: "ProvisionOperations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "TerminalSuccessAt",
                table: "ProvisionOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TerminalSuccessSequence",
                table: "ProvisionOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AuthorityGeneration",
                table: "ProvisionEffects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CanonicalWorkspaceIdentity",
                table: "ProvisionEffects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ClaimGeneration",
                table: "ProvisionEffects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnrollmentId",
                table: "ProvisionEffects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IncarnationId",
                table: "ProvisionEffects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntentDigest",
                table: "ProvisionEffects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ObservationId",
                table: "ProvisionEffects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyId",
                table: "ProvisionEffects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PolicyRevision",
                table: "ProvisionEffects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReservationGrantGeneration",
                table: "ProvisionEffects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReservationId",
                table: "ProvisionEffects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReservationRevision",
                table: "ProvisionEffects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceId",
                table: "ProvisionEffects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ClaimGeneration",
                table: "ProvisionAttempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ClaimedAt",
                table: "ProvisionAttempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "EnrollmentId",
                table: "ProvisionAttempts",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ExecutorAuthorityGeneration",
                table: "ProvisionAttempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "IncarnationId",
                table: "ProvisionAttempts",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "ReservationGrantGeneration",
                table: "ProvisionAttempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "EffectObservationId",
                table: "HostResourceReservations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProvisionExecutorReports",
                columns: table => new
                {
                    ReportId = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Disposition = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisionExecutorReports", x => x.ReportId);
                    table.ForeignKey(
                        name: "FK_ProvisionExecutorReports_ProvisionOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "ProvisionOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionEffects_ReservationId_Effect",
                table: "ProvisionEffects",
                columns: new[] { "ReservationId", "Effect" },
                unique: true,
                filter: "ReservationId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HostResourceReservations_EffectObservationId",
                table: "HostResourceReservations",
                column: "EffectObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionExecutorReports_OperationId_ClaimGeneration_Kind_Sequence",
                table: "ProvisionExecutorReports",
                columns: new[] { "OperationId", "ClaimGeneration", "Kind", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionExecutorReports_OperationId_ClaimGeneration_Sequence",
                table: "ProvisionExecutorReports",
                columns: new[] { "OperationId", "ClaimGeneration", "Sequence" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_HostResourceReservations_HostResourceObservations_EffectObservationId",
                table: "HostResourceReservations",
                column: "EffectObservationId",
                principalTable: "HostResourceObservations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HostResourceReservations_HostResourceObservations_EffectObservationId",
                table: "HostResourceReservations");

            migrationBuilder.DropTable(
                name: "ProvisionExecutorReports");

            migrationBuilder.DropIndex(
                name: "IX_ProvisionEffects_ReservationId_Effect",
                table: "ProvisionEffects");

            migrationBuilder.DropIndex(
                name: "IX_HostResourceReservations_EffectObservationId",
                table: "HostResourceReservations");

            migrationBuilder.DropColumn(
                name: "ClaimGeneration",
                table: "ProvisionOperations");

            migrationBuilder.DropColumn(
                name: "LastAppliedResultSequence",
                table: "ProvisionOperations");

            migrationBuilder.DropColumn(
                name: "SelectedAuthorityGeneration",
                table: "ProvisionOperations");

            migrationBuilder.DropColumn(
                name: "SelectedEnrollmentId",
                table: "ProvisionOperations");

            migrationBuilder.DropColumn(
                name: "SelectedIncarnationId",
                table: "ProvisionOperations");

            migrationBuilder.DropColumn(
                name: "TerminalSuccessAt",
                table: "ProvisionOperations");

            migrationBuilder.DropColumn(
                name: "TerminalSuccessSequence",
                table: "ProvisionOperations");

            migrationBuilder.DropColumn(
                name: "AuthorityGeneration",
                table: "ProvisionEffects");

            migrationBuilder.DropColumn(
                name: "CanonicalWorkspaceIdentity",
                table: "ProvisionEffects");

            migrationBuilder.DropColumn(
                name: "ClaimGeneration",
                table: "ProvisionEffects");

            migrationBuilder.DropColumn(
                name: "EnrollmentId",
                table: "ProvisionEffects");

            migrationBuilder.DropColumn(
                name: "IncarnationId",
                table: "ProvisionEffects");

            migrationBuilder.DropColumn(
                name: "IntentDigest",
                table: "ProvisionEffects");

            migrationBuilder.DropColumn(
                name: "ObservationId",
                table: "ProvisionEffects");

            migrationBuilder.DropColumn(
                name: "PolicyId",
                table: "ProvisionEffects");

            migrationBuilder.DropColumn(
                name: "PolicyRevision",
                table: "ProvisionEffects");

            migrationBuilder.DropColumn(
                name: "ReservationGrantGeneration",
                table: "ProvisionEffects");

            migrationBuilder.DropColumn(
                name: "ReservationId",
                table: "ProvisionEffects");

            migrationBuilder.DropColumn(
                name: "ReservationRevision",
                table: "ProvisionEffects");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "ProvisionEffects");

            migrationBuilder.DropColumn(
                name: "ClaimGeneration",
                table: "ProvisionAttempts");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "ProvisionAttempts");

            migrationBuilder.DropColumn(
                name: "EnrollmentId",
                table: "ProvisionAttempts");

            migrationBuilder.DropColumn(
                name: "ExecutorAuthorityGeneration",
                table: "ProvisionAttempts");

            migrationBuilder.DropColumn(
                name: "IncarnationId",
                table: "ProvisionAttempts");

            migrationBuilder.DropColumn(
                name: "ReservationGrantGeneration",
                table: "ProvisionAttempts");

            migrationBuilder.DropColumn(
                name: "EffectObservationId",
                table: "HostResourceReservations");
        }
    }
}
