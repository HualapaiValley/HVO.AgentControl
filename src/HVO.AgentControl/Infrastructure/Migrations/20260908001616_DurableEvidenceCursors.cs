using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableEvidenceCursors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EvidenceConsumerCursors",
                columns: table => new
                {
                    ConsumerId = table.Column<string>(type: "TEXT", nullable: false),
                    LastConsumedSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    HistoryGap = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceConsumerCursors", x => x.ConsumerId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvidenceConsumerCursors");
        }
    }
}
