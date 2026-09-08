using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TaskBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkerSlots",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerSlots", x => x.Sequence);
                    table.UniqueConstraint("AK_WorkerSlots_Id", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkerSlots_Runtimes_RuntimeId",
                        column: x => x.RuntimeId,
                        principalTable: "Runtimes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskWorkspaces",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerSlotId = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<string>(type: "TEXT", nullable: false),
                    Directory = table.Column<string>(type: "TEXT", nullable: false),
                    Branch = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskWorkspaces", x => x.Sequence);
                    table.UniqueConstraint("AK_TaskWorkspaces_Id", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskWorkspaces_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskWorkspaces_Runtimes_RuntimeId",
                        column: x => x.RuntimeId,
                        principalTable: "Runtimes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskWorkspaces_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskWorkspaces_WorkerSlots_WorkerSlotId",
                        column: x => x.WorkerSlotId,
                        principalTable: "WorkerSlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskBindings",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: false),
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerSlotId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionBindingId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    PlacementVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskBindings", x => x.Sequence);
                    table.UniqueConstraint("AK_TaskBindings_Id", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskBindings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskBindings_TaskWorkspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "TaskWorkspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskBindings_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskBindings_WorkerSlots_WorkerSlotId",
                        column: x => x.WorkerSlotId,
                        principalTable: "WorkerSlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskSessionBindings",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Id = table.Column<string>(type: "TEXT", nullable: true),
                    TaskBindingId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerSlotId = table.Column<string>(type: "TEXT", nullable: false),
                    LegacyWorkerId = table.Column<string>(type: "TEXT", nullable: true),
                    NativeSessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Generation = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskSessionBindings", x => x.Sequence);
                    table.ForeignKey(
                        name: "FK_TaskSessionBindings_TaskBindings_TaskBindingId",
                        column: x => x.TaskBindingId,
                        principalTable: "TaskBindings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskSessionBindings_WorkerSlots_WorkerSlotId",
                        column: x => x.WorkerSlotId,
                        principalTable: "WorkerSlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskSessionBindings_Workers_LegacyWorkerId",
                        column: x => x.LegacyWorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskBindings_Id",
                table: "TaskBindings",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskBindings_ProjectId",
                table: "TaskBindings",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskBindings_WorkerSlotId",
                table: "TaskBindings",
                column: "WorkerSlotId",
                unique: true,
                filter: "State = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_TaskBindings_WorkItemId",
                table: "TaskBindings",
                column: "WorkItemId",
                unique: true,
                filter: "State = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_TaskBindings_WorkspaceId",
                table: "TaskBindings",
                column: "WorkspaceId",
                unique: true,
                filter: "State = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_TaskSessionBindings_Id",
                table: "TaskSessionBindings",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskSessionBindings_LegacyWorkerId",
                table: "TaskSessionBindings",
                column: "LegacyWorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskSessionBindings_NativeSessionId",
                table: "TaskSessionBindings",
                column: "NativeSessionId",
                unique: true,
                filter: "NativeSessionId <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_TaskSessionBindings_TaskBindingId",
                table: "TaskSessionBindings",
                column: "TaskBindingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskSessionBindings_WorkerSlotId",
                table: "TaskSessionBindings",
                column: "WorkerSlotId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskWorkspaces_Id",
                table: "TaskWorkspaces",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskWorkspaces_ProjectId",
                table: "TaskWorkspaces",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskWorkspaces_RuntimeId_Directory",
                table: "TaskWorkspaces",
                columns: new[] { "RuntimeId", "Directory" },
                unique: true,
                filter: "State = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_TaskWorkspaces_WorkerSlotId",
                table: "TaskWorkspaces",
                column: "WorkerSlotId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskWorkspaces_WorkItemId",
                table: "TaskWorkspaces",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerSlots_Id",
                table: "WorkerSlots",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkerSlots_RuntimeId_Name",
                table: "WorkerSlots",
                columns: new[] { "RuntimeId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskSessionBindings");

            migrationBuilder.DropTable(
                name: "TaskBindings");

            migrationBuilder.DropTable(
                name: "TaskWorkspaces");

            migrationBuilder.DropTable(
                name: "WorkerSlots");
        }
    }
}
