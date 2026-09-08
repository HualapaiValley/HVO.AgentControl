using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WorkItemLiveRepositoryBranch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_Repository_Branch",
                table: "WorkItems",
                columns: new[] { "Repository", "Branch" },
                unique: true,
                filter: "State NOT IN ('Released', 'Abandoned')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkItems_Repository_Branch",
                table: "WorkItems");
        }
    }
}
