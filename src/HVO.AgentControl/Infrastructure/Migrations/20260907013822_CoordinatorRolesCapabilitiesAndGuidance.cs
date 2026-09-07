using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CoordinatorRolesCapabilitiesAndGuidance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CapabilitiesJson",
                table: "Workers",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "CapabilityCommandId",
                table: "Workers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CapabilityReport",
                table: "Workers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "CapabilityReportedAt",
                table: "Workers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Workers",
                type: "TEXT",
                nullable: false,
                defaultValue: "Worker");

            migrationBuilder.AddColumn<string>(
                name: "CapabilitiesJson",
                table: "Runtimes",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<bool>(
                name: "IncludeGuidance",
                table: "CoordinationRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "LastDecisionAt",
                table: "CoordinationRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "ProgressMinutes",
                table: "CoordinationRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AcceptedAt",
                table: "Commands",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastProgressAt",
                table: "Commands",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProgressText",
                table: "Commands",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
            // Preserve established routing identities; never infer a role from a display name.
            migrationBuilder.Sql("UPDATE Workers SET Role = 'Coordinator' WHERE Id IN (SELECT CoordinatorWorkerId FROM CoordinationRuns) OR Id IN (SELECT WorkerId FROM Commands WHERE Origin LIKE 'coordinator-decision:%');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CapabilitiesJson",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "CapabilityCommandId",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "CapabilityReport",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "CapabilityReportedAt",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "CapabilitiesJson",
                table: "Runtimes");

            migrationBuilder.DropColumn(
                name: "IncludeGuidance",
                table: "CoordinationRuns");

            migrationBuilder.DropColumn(
                name: "LastDecisionAt",
                table: "CoordinationRuns");

            migrationBuilder.DropColumn(
                name: "ProgressMinutes",
                table: "CoordinationRuns");

            migrationBuilder.DropColumn(
                name: "AcceptedAt",
                table: "Commands");

            migrationBuilder.DropColumn(
                name: "LastProgressAt",
                table: "Commands");

            migrationBuilder.DropColumn(
                name: "ProgressText",
                table: "Commands");
        }
    }
}
