using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableEvidenceReadReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EvidenceReadReceipts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ConsumerId = table.Column<string>(type: "TEXT", nullable: false),
                    AfterSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    NextSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    PageJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AcknowledgedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceReadReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceReadReceipts_ConsumerId_AcknowledgedAt",
                table: "EvidenceReadReceipts",
                columns: new[] { "ConsumerId", "AcknowledgedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvidenceReadReceipts");
        }
    }
}
