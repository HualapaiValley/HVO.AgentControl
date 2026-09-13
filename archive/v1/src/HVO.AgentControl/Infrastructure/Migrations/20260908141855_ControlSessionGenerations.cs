using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ControlSessionGenerations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ControlSessions_ScopeKind_ScopeId",
                table: "ControlSessions");

            migrationBuilder.AddColumn<int>(
                name: "Generation",
                table: "ControlSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsCurrent",
                table: "ControlSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "PredecessorId",
                table: "ControlSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ControlSessions_PredecessorId",
                table: "ControlSessions",
                column: "PredecessorId",
                unique: true,
                filter: "PredecessorId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ControlSessions_ScopeKind_ScopeId",
                table: "ControlSessions",
                columns: new[] { "ScopeKind", "ScopeId" },
                unique: true,
                filter: "IsCurrent = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ControlSessions_ScopeKind_ScopeId_Generation",
                table: "ControlSessions",
                columns: new[] { "ScopeKind", "ScopeId", "Generation" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ControlSessions_PredecessorId",
                table: "ControlSessions");

            migrationBuilder.DropIndex(
                name: "IX_ControlSessions_ScopeKind_ScopeId",
                table: "ControlSessions");

            migrationBuilder.DropIndex(
                name: "IX_ControlSessions_ScopeKind_ScopeId_Generation",
                table: "ControlSessions");

            migrationBuilder.DropColumn(
                name: "Generation",
                table: "ControlSessions");

            migrationBuilder.DropColumn(
                name: "IsCurrent",
                table: "ControlSessions");

            migrationBuilder.DropColumn(
                name: "PredecessorId",
                table: "ControlSessions");

            migrationBuilder.CreateIndex(
                name: "IX_ControlSessions_ScopeKind_ScopeId",
                table: "ControlSessions",
                columns: new[] { "ScopeKind", "ScopeId" },
                unique: true);
        }
    }
}
