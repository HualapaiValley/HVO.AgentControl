using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModelContextObservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModelCatalogObservations",
                columns: table => new
                {
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelCatalogObservations", x => x.RuntimeId);
                    table.ForeignKey("FK_ModelCatalogObservations_Runtimes_RuntimeId", x => x.RuntimeId, "Runtimes", "Id", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ModelUsageProvenance",
                columns: table => new
                {
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    NativeSessionId = table.Column<string>(type: "TEXT", nullable: false),
                    NativeMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    EffectiveInputTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    ParentMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    IsSummary = table.Column<bool>(type: "INTEGER", nullable: false),
                    FinishReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelUsageProvenance", x => new { x.RuntimeId, x.NativeSessionId, x.NativeMessageId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelUsageProvenance");

            migrationBuilder.DropTable(
                name: "ModelCatalogObservations");
        }
    }
}
