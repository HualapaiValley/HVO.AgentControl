using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProviderPoolCircuit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderPoolId",
                table: "Commands",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ProviderFailureReceipt",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PoolId = table.Column<string>(type: "TEXT", nullable: false),
                    CommandId = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: true),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RetryAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderFailureReceipt", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProviderPool",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    RetryAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    LastCommandId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderPool", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderFailureReceipt");

            migrationBuilder.DropTable(
                name: "ProviderPool");

            migrationBuilder.DropColumn(
                name: "ProviderPoolId",
                table: "Commands");
        }
    }
}
