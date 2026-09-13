using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RuntimeTelemetryHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelemetryHistory",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CpuQuotaPercent = table.Column<double>(type: "REAL", nullable: true),
                    CpuCoreUsage = table.Column<double>(type: "REAL", nullable: true),
                    MemoryPercent = table.Column<double>(type: "REAL", nullable: true),
                    QuotaCores = table.Column<double>(type: "REAL", nullable: true),
                    CpuWindowMs = table.Column<long>(type: "INTEGER", nullable: true),
                    MemoryBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    MemoryLimitBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryHistory", x => x.Sequence);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryHistory_RuntimeId_ObservedAt_Sequence",
                table: "TelemetryHistory",
                columns: new[] { "RuntimeId", "ObservedAt", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelemetryHistory");
        }
    }
}
