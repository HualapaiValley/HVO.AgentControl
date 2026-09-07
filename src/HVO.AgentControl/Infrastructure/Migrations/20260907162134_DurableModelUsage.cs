using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableModelUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModelUsage",
                columns: table => new
                {
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    NativeSessionId = table.Column<string>(type: "TEXT", nullable: false),
                    NativeMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerId = table.Column<string>(type: "TEXT", nullable: true),
                    SessionRole = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TotalTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    ReasoningTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    CacheWriteTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    ProviderCost = table.Column<decimal>(type: "TEXT", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", nullable: true),
                    CostProvenance = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SeenInTranscript = table.Column<bool>(type: "INTEGER", nullable: false),
                    SeenInCommandResult = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelUsage", x => new { x.RuntimeId, x.NativeSessionId, x.NativeMessageId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModelUsage_ProviderId_ModelId_CreatedAt",
                table: "ModelUsage",
                columns: new[] { "ProviderId", "ModelId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelUsage_WorkerId_CreatedAt",
                table: "ModelUsage",
                columns: new[] { "WorkerId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelUsage");
        }
    }
}
