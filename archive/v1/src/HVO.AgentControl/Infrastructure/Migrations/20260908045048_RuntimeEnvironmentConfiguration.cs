using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RuntimeEnvironmentConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Projects_Id",
                table: "Projects",
                column: "Id");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Hosts_Id",
                table: "Hosts",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "RuntimeEnvironments",
                columns: table => new
                {
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    HostId = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationProjectId = table.Column<string>(type: "TEXT", nullable: true),
                    DevcontainerPath = table.Column<string>(type: "TEXT", nullable: true),
                    ConnectionFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeEnvironments", x => x.RuntimeId);
                    table.ForeignKey(
                        name: "FK_RuntimeEnvironments_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RuntimeEnvironments_Projects_ConfigurationProjectId",
                        column: x => x.ConfigurationProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RuntimeEnvironments_Runtimes_RuntimeId",
                        column: x => x.RuntimeId,
                        principalTable: "Runtimes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeEnvironments_ConfigurationProjectId",
                table: "RuntimeEnvironments",
                column: "ConfigurationProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeEnvironments_HostId",
                table: "RuntimeEnvironments",
                column: "HostId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimeEnvironments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Projects_Id",
                table: "Projects");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Hosts_Id",
                table: "Hosts");
        }
    }
}
