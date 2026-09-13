using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WorkerLifecycleAndCoordination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Agent",
                table: "Workers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Archived",
                table: "Workers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Workers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ModelsJson",
                table: "Workers",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<long>(
                name: "SettingsRevision",
                table: "Workers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Variant",
                table: "Workers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExecutionPayload",
                table: "Commands",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "CoordinationRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CoordinatorWorkerId = table.Column<string>(type: "TEXT", nullable: false),
                    Instruction = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    DecisionCommandId = table.Column<string>(type: "TEXT", nullable: true),
                    InputJson = table.Column<string>(type: "TEXT", nullable: false),
                    DecisionJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastObservation = table.Column<string>(type: "TEXT", nullable: false),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxRounds = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoordinationRuns", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoordinationRuns");

            migrationBuilder.DropColumn(
                name: "Agent",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "Archived",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "ModelsJson",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "SettingsRevision",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "Variant",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "ExecutionPayload",
                table: "Commands");
        }
    }
}
