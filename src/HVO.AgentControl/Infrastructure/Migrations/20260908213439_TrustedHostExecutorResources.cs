using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrustedHostExecutorResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostExecutors",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    HostId = table.Column<string>(type: "TEXT", nullable: false),
                    EndpointId = table.Column<string>(type: "TEXT", nullable: false),
                    PhysicalHostId = table.Column<string>(type: "TEXT", nullable: false),
                    EngineId = table.Column<string>(type: "TEXT", nullable: false),
                    BuilderId = table.Column<string>(type: "TEXT", nullable: false),
                    CliBundleDigest = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedRootDigest = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorityDigest = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialDigest = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorityGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    BootId = table.Column<string>(type: "TEXT", nullable: false),
                    IncarnationId = table.Column<string>(type: "TEXT", nullable: false),
                    LastSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ActivatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostExecutors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostExecutors_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HostResourceMutations",
                columns: table => new
                {
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostResourceMutations", x => x.RequestId);
                });

            migrationBuilder.CreateTable(
                name: "HostResourceObservations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EnrollmentId = table.Column<string>(type: "TEXT", nullable: false),
                    HostId = table.Column<string>(type: "TEXT", nullable: false),
                    EndpointId = table.Column<string>(type: "TEXT", nullable: false),
                    PhysicalHostId = table.Column<string>(type: "TEXT", nullable: false),
                    EngineId = table.Column<string>(type: "TEXT", nullable: false),
                    BuilderId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorityGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    BootId = table.Column<string>(type: "TEXT", nullable: false),
                    IncarnationId = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    PhysicalRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CollectedFrom = table.Column<long>(type: "INTEGER", nullable: false),
                    CollectedTo = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Architecture = table.Column<string>(type: "TEXT", nullable: false),
                    EffectiveCpuMillis = table.Column<long>(type: "INTEGER", nullable: true),
                    AvailableCpuMillis = table.Column<long>(type: "INTEGER", nullable: true),
                    EffectiveMemoryBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    AvailableMemoryBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    SwapUsedBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    SwapLimitBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    NativeSessionBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CompilerPeakBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    MemoryPressureEvents = table.Column<long>(type: "INTEGER", nullable: true),
                    WorkspaceId = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalWorkspaceIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspaceFilesystemId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspaceAvailableBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    WorkspaceAvailableInodes = table.Column<long>(type: "INTEGER", nullable: true),
                    DockerFilesystemId = table.Column<string>(type: "TEXT", nullable: true),
                    DockerAvailableBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    DockerAvailableInodes = table.Column<long>(type: "INTEGER", nullable: true),
                    DockerAvailable = table.Column<bool>(type: "INTEGER", nullable: true),
                    BuildSlots = table.Column<int>(type: "INTEGER", nullable: true),
                    ExternalOwnershipState = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalOwnershipIntentDigest = table.Column<string>(type: "TEXT", nullable: true),
                    EvidenceDigest = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostResourceObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostResourceObservations_HostExecutors_EnrollmentId",
                        column: x => x.EnrollmentId,
                        principalTable: "HostExecutors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HostResourcePolicies",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    EnrollmentId = table.Column<string>(type: "TEXT", nullable: false),
                    PhysicalHostId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    MaxCpuMillis = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxMemoryBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxDiskBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    BuildSlots = table.Column<int>(type: "INTEGER", nullable: false),
                    ControllerReserveCpuMillis = table.Column<long>(type: "INTEGER", nullable: false),
                    ControllerReserveMemoryBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ControllerReserveDiskBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ObservationMaxAgeSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    GrantLifetimeSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostResourcePolicies", x => new { x.Id, x.Revision });
                    table.ForeignKey(
                        name: "FK_HostResourcePolicies_HostExecutors_EnrollmentId",
                        column: x => x.EnrollmentId,
                        principalTable: "HostExecutors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HostResourceReservations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    IntentDigest = table.Column<string>(type: "TEXT", nullable: false),
                    HostId = table.Column<string>(type: "TEXT", nullable: false),
                    PhysicalHostId = table.Column<string>(type: "TEXT", nullable: false),
                    EndpointId = table.Column<string>(type: "TEXT", nullable: false),
                    EnrollmentId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorityGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    IncarnationId = table.Column<string>(type: "TEXT", nullable: false),
                    PolicyId = table.Column<string>(type: "TEXT", nullable: false),
                    PolicyRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ObservationId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalWorkspaceIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspaceFilesystemId = table.Column<string>(type: "TEXT", nullable: false),
                    DockerFilesystemId = table.Column<string>(type: "TEXT", nullable: true),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    CpuMillis = table.Column<long>(type: "INTEGER", nullable: false),
                    MemoryBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DiskBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    BuildSlots = table.Column<int>(type: "INTEGER", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: true),
                    SharedResourceKey = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    GrantGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    GrantedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    GrantExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EffectCommittedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ReleasedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ReleaseObservationId = table.Column<string>(type: "TEXT", nullable: true),
                    ReleaseEvidence = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostResourceReservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostResourceReservations_HostExecutors_EnrollmentId",
                        column: x => x.EnrollmentId,
                        principalTable: "HostExecutors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HostResourceReservations_HostResourceObservations_ObservationId",
                        column: x => x.ObservationId,
                        principalTable: "HostResourceObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HostResourceReservations_HostResourceObservations_ReleaseObservationId",
                        column: x => x.ReleaseObservationId,
                        principalTable: "HostResourceObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HostResourceReservations_HostResourcePolicies_PolicyId_PolicyRevision",
                        columns: x => new { x.PolicyId, x.PolicyRevision },
                        principalTable: "HostResourcePolicies",
                        principalColumns: new[] { "Id", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostExecutors_EndpointId",
                table: "HostExecutors",
                column: "EndpointId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostExecutors_EngineId",
                table: "HostExecutors",
                column: "EngineId");

            migrationBuilder.CreateIndex(
                name: "IX_HostExecutors_HostId",
                table: "HostExecutors",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_HostExecutors_RequestId",
                table: "HostExecutors",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostResourceObservations_EnrollmentId_Sequence",
                table: "HostResourceObservations",
                columns: new[] { "EnrollmentId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostResourceObservations_EnrollmentId_WorkspaceId_Sequence",
                table: "HostResourceObservations",
                columns: new[] { "EnrollmentId", "WorkspaceId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_HostResourceObservations_PhysicalHostId_PhysicalRevision",
                table: "HostResourceObservations",
                columns: new[] { "PhysicalHostId", "PhysicalRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostResourcePolicies_EnrollmentId",
                table: "HostResourcePolicies",
                column: "EnrollmentId");

            migrationBuilder.CreateIndex(
                name: "IX_HostResourcePolicies_PhysicalHostId_Revision",
                table: "HostResourcePolicies",
                columns: new[] { "PhysicalHostId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostResourcePolicies_RequestId",
                table: "HostResourcePolicies",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostResourceReservations_EnrollmentId",
                table: "HostResourceReservations",
                column: "EnrollmentId");

            migrationBuilder.CreateIndex(
                name: "IX_HostResourceReservations_ObservationId",
                table: "HostResourceReservations",
                column: "ObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_HostResourceReservations_PhysicalHostId_CanonicalWorkspaceIdentity",
                table: "HostResourceReservations",
                columns: new[] { "PhysicalHostId", "CanonicalWorkspaceIdentity" },
                unique: true,
                filter: "State IN ('Held', 'EffectCommitted', 'Unknown')");

            migrationBuilder.CreateIndex(
                name: "IX_HostResourceReservations_PhysicalHostId_Port",
                table: "HostResourceReservations",
                columns: new[] { "PhysicalHostId", "Port" },
                unique: true,
                filter: "Port IS NOT NULL AND State IN ('Held', 'EffectCommitted', 'Unknown')");

            migrationBuilder.CreateIndex(
                name: "IX_HostResourceReservations_PhysicalHostId_SharedResourceKey",
                table: "HostResourceReservations",
                columns: new[] { "PhysicalHostId", "SharedResourceKey" },
                unique: true,
                filter: "SharedResourceKey IS NOT NULL AND State IN ('Held', 'EffectCommitted', 'Unknown')");

            migrationBuilder.CreateIndex(
                name: "IX_HostResourceReservations_PolicyId_PolicyRevision",
                table: "HostResourceReservations",
                columns: new[] { "PolicyId", "PolicyRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_HostResourceReservations_ReleaseObservationId",
                table: "HostResourceReservations",
                column: "ReleaseObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_HostResourceReservations_RequestId",
                table: "HostResourceReservations",
                column: "RequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostResourceMutations");

            migrationBuilder.DropTable(
                name: "HostResourceReservations");

            migrationBuilder.DropTable(
                name: "HostResourceObservations");

            migrationBuilder.DropTable(
                name: "HostResourcePolicies");

            migrationBuilder.DropTable(
                name: "HostExecutors");
        }
    }
}
