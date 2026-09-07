using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableWorkspaceClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkspaceClaims",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CommandId = table.Column<string>(type: "TEXT", nullable: false),
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    Directory = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceClaims", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkspaceClaims");
        }
    }
}
