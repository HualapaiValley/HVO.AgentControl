using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FallbackFailureProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceFailureCategory",
                table: "ProviderFallbackReceipt",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceFailureReceiptId",
                table: "ProviderFallbackReceipt",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceTerminalState",
                table: "ProviderFallbackReceipt",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceFailureCategory",
                table: "ProviderFallbackReceipt");

            migrationBuilder.DropColumn(
                name: "SourceFailureReceiptId",
                table: "ProviderFallbackReceipt");

            migrationBuilder.DropColumn(
                name: "SourceTerminalState",
                table: "ProviderFallbackReceipt");
        }
    }
}
