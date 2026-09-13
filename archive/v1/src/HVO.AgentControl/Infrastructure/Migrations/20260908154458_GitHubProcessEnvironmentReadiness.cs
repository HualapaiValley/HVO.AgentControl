using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GitHubProcessEnvironmentReadiness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CredentialConfigurationFingerprint",
                table: "GitHubAccess",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CredentialState",
                table: "GitHubAccess",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "EnvironmentPolicyFingerprint",
                table: "GitHubAccess",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "EnvironmentPolicyVersion",
                table: "GitHubAccess",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EnvironmentProcessId",
                table: "GitHubAccess",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnvironmentProcessIncarnation",
                table: "GitHubAccess",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "EnvironmentVerifiedAt",
                table: "GitHubAccess",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CredentialConfigurationFingerprint",
                table: "GitHubAccess");

            migrationBuilder.DropColumn(
                name: "CredentialState",
                table: "GitHubAccess");

            migrationBuilder.DropColumn(
                name: "EnvironmentPolicyFingerprint",
                table: "GitHubAccess");

            migrationBuilder.DropColumn(
                name: "EnvironmentPolicyVersion",
                table: "GitHubAccess");

            migrationBuilder.DropColumn(
                name: "EnvironmentProcessId",
                table: "GitHubAccess");

            migrationBuilder.DropColumn(
                name: "EnvironmentProcessIncarnation",
                table: "GitHubAccess");

            migrationBuilder.DropColumn(
                name: "EnvironmentVerifiedAt",
                table: "GitHubAccess");
        }
    }
}
