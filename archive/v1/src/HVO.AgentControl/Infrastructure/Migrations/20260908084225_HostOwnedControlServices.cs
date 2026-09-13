using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HostOwnedControlServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Workers_RuntimeId_Directory",
                table: "Workers");

            migrationBuilder.AddColumn<string>(
                name: "ConnectionKind",
                table: "Runtimes",
                type: "TEXT",
                nullable: false,
                defaultValue: "Ssh");

            migrationBuilder.CreateTable(
                name: "ControlServices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    IncarnationId = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<string>(type: "TEXT", nullable: false),
                    LastObservedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ControlServices_Runtimes_Id",
                        column: x => x.Id,
                        principalTable: "Runtimes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ControlSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ControlServiceId = table.Column<string>(type: "TEXT", nullable: false),
                    ScopeKind = table.Column<string>(type: "TEXT", nullable: false),
                    ScopeId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerId = table.Column<string>(type: "TEXT", nullable: false),
                    NativeSessionId = table.Column<string>(type: "TEXT", nullable: false),
                    CreationCommandId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ControlSessions_ControlServices_ControlServiceId",
                        column: x => x.ControlServiceId,
                        principalTable: "ControlServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workers_RuntimeId_Directory",
                table: "Workers",
                columns: new[] { "RuntimeId", "Directory" },
                unique: true,
                filter: "Role != 'Coordinator'");

            migrationBuilder.CreateIndex(
                name: "IX_ControlServices_InstanceId",
                table: "ControlServices",
                column: "InstanceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ControlSessions_ControlServiceId",
                table: "ControlSessions",
                column: "ControlServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ControlSessions_ScopeKind_ScopeId",
                table: "ControlSessions",
                columns: new[] { "ScopeKind", "ScopeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ControlSessions_WorkerId",
                table: "ControlSessions",
                column: "WorkerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ControlSessions");

            migrationBuilder.DropTable(
                name: "ControlServices");

            migrationBuilder.DropIndex(
                name: "IX_Workers_RuntimeId_Directory",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "ConnectionKind",
                table: "Runtimes");

            migrationBuilder.CreateIndex(
                name: "IX_Workers_RuntimeId_Directory",
                table: "Workers",
                columns: new[] { "RuntimeId", "Directory" },
                unique: true);
        }
    }
}
