using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableProvisioningOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProvisionOperations",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedJson = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedIntentJson = table.Column<string>(type: "TEXT", nullable: false),
                    IntentDigest = table.Column<string>(type: "TEXT", nullable: false),
                    HostId = table.Column<string>(type: "TEXT", nullable: false),
                    HostRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    RuntimeRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    EnvironmentRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceRevision = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationPath = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedWorkspaceIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    ColdBuild = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequestedBuildCpuMillis = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestedBuildMemoryBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestedRuntimeCpuMillis = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestedRuntimeMemoryBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthorityRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    CapacityReservationId = table.Column<string>(type: "TEXT", nullable: false),
                    CapacityRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    CapacityValidUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    ReservedBuildCpuMillis = table.Column<long>(type: "INTEGER", nullable: true),
                    ReservedBuildMemoryBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    ReservedRuntimeCpuMillis = table.Column<long>(type: "INTEGER", nullable: true),
                    ReservedRuntimeMemoryBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    CancelRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReconcileRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    EffectStarted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProgressJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedContainerId = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedImageId = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisionOperations", x => x.Sequence);
                    table.UniqueConstraint("AK_ProvisionOperations_Id", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProvisionAttempts",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    IntentDigest = table.Column<string>(type: "TEXT", nullable: false),
                    IntentJson = table.Column<string>(type: "TEXT", nullable: false),
                    HostId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspaceIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorityRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    CapacityReservationId = table.Column<string>(type: "TEXT", nullable: false),
                    CapacityRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    CapacityFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisionAttempts", x => x.OperationId);
                    table.ForeignKey(
                        name: "FK_ProvisionAttempts_ProvisionOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "ProvisionOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProvisionEffects",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    Effect = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisionEffects", x => new { x.OperationId, x.Effect });
                    table.ForeignKey(
                        name: "FK_ProvisionEffects_ProvisionAttempts_OperationId",
                        column: x => x.OperationId,
                        principalTable: "ProvisionAttempts",
                        principalColumn: "OperationId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionAttempts_HostId_WorkspaceIdentity",
                table: "ProvisionAttempts",
                columns: new[] { "HostId", "WorkspaceIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionOperations_HostId_State",
                table: "ProvisionOperations",
                columns: new[] { "HostId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionOperations_Id",
                table: "ProvisionOperations",
                column: "Id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProvisionEffects");

            migrationBuilder.DropTable(
                name: "ProvisionAttempts");

            migrationBuilder.DropTable(
                name: "ProvisionOperations");
        }
    }
}
