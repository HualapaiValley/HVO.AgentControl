using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContinuousCoordinationSupervision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BudgetWindowEndsAt",
                table: "CoordinationRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "ContinuousSupervision",
                table: "CoordinationRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "LastSupervisorAt",
                table: "CoordinationRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "TurnWindowMinutes",
                table: "CoordinationRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TurnsPerWindow",
                table: "CoordinationRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE CoordinationRuns SET TurnsPerWindow = MaxRounds, TurnWindowMinutes = 60");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BudgetWindowEndsAt",
                table: "CoordinationRuns");

            migrationBuilder.DropColumn(
                name: "ContinuousSupervision",
                table: "CoordinationRuns");

            migrationBuilder.DropColumn(
                name: "LastSupervisorAt",
                table: "CoordinationRuns");

            migrationBuilder.DropColumn(
                name: "TurnWindowMinutes",
                table: "CoordinationRuns");

            migrationBuilder.DropColumn(
                name: "TurnsPerWindow",
                table: "CoordinationRuns");
        }
    }
}
