using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExplicitHostKeyAlgorithm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HostKeyAlgorithm",
                table: "Runtimes",
                type: "TEXT",
                nullable: false,
                defaultValue: "ssh-ed25519");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HostKeyAlgorithm",
                table: "Runtimes");
        }
    }
}
