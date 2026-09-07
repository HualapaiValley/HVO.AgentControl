using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableOperatorUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperatorStatusUpdates",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduleId = table.Column<string>(type: "TEXT", nullable: false),
                    CoordinationRunId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    DueAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PublishedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceEventSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ActiveSetRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    MissedIntervals = table.Column<long>(type: "INTEGER", nullable: false),
                    SummaryJson = table.Column<string>(type: "TEXT", nullable: false),
                    AcknowledgedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorStatusUpdates", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "OperatorUpdateSchedules",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CoordinationRunId = table.Column<string>(type: "TEXT", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextDueAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastDueAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastEmittedEventSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ActiveSetRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorUpdateSchedules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperatorStatusUpdates_CoordinationRunId_Sequence",
                table: "OperatorStatusUpdates",
                columns: new[] { "CoordinationRunId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_OperatorStatusUpdates_Id",
                table: "OperatorStatusUpdates",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperatorStatusUpdates_ScheduleId_Kind_DueAt",
                table: "OperatorStatusUpdates",
                columns: new[] { "ScheduleId", "Kind", "DueAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperatorUpdateSchedules_CoordinationRunId",
                table: "OperatorUpdateSchedules",
                column: "CoordinationRunId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperatorStatusUpdates");

            migrationBuilder.DropTable(
                name: "OperatorUpdateSchedules");
        }
    }
}
