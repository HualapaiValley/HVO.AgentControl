using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GitHubCiReadPermissionEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActionsPermission",
                table: "GitHubAccess",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "ChecksPermission",
                table: "GitHubAccess",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "CommitStatusesPermission",
                table: "GitHubAccess",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<long>(
                name: "PermissionsVerifiedAt",
                table: "GitHubAccess",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActionsPermission",
                table: "GitHubAccess");

            migrationBuilder.DropColumn(
                name: "ChecksPermission",
                table: "GitHubAccess");

            migrationBuilder.DropColumn(
                name: "CommitStatusesPermission",
                table: "GitHubAccess");

            migrationBuilder.DropColumn(
                name: "PermissionsVerifiedAt",
                table: "GitHubAccess");
        }
    }
}
