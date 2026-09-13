using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnrollmentAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommandAuthorities",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CommandId = table.Column<string>(type: "TEXT", nullable: false),
                    EnrollmentId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorityGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AcknowledgedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    AcknowledgementData = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandAuthorities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Enrollments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AdapterType = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorityGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    EnrolledAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Enrollments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceCursors",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EnrollmentId = table.Column<string>(type: "TEXT", nullable: false),
                    CursorName = table.Column<string>(type: "TEXT", nullable: false),
                    CursorValue = table.Column<string>(type: "TEXT", nullable: false),
                    LastConsumedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAcknowledgedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceCursors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommandAuthorities_CommandId",
                table: "CommandAuthorities",
                column: "CommandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommandAuthorities_EnrollmentId",
                table: "CommandAuthorities",
                column: "EnrollmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Enrollments_AdapterType",
                table: "Enrollments",
                column: "AdapterType");

            migrationBuilder.CreateIndex(
                name: "IX_Enrollments_State",
                table: "Enrollments",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceCursors_EnrollmentId_CursorName",
                table: "EvidenceCursors",
                columns: new[] { "EnrollmentId", "CursorName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommandAuthorities");

            migrationBuilder.DropTable(
                name: "Enrollments");

            migrationBuilder.DropTable(
                name: "EvidenceCursors");
        }
    }
}
