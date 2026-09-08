using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableGitHubMergeIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GitHubCheckObservations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Repository = table.Column<string>(type: "TEXT", nullable: false),
                    PullRequestNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    HeadSha = table.Column<string>(type: "TEXT", nullable: false),
                    BaseSha = table.Column<string>(type: "TEXT", nullable: false),
                    PullRequestState = table.Column<string>(type: "TEXT", nullable: false),
                    Mergeable = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuthorIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedReviewersJson = table.Column<string>(type: "TEXT", nullable: false),
                    UnresolvedReviewThreads = table.Column<int>(type: "INTEGER", nullable: false),
                    ChecksJson = table.Column<string>(type: "TEXT", nullable: false),
                    StatusesJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubCheckObservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitHubMergeIntents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Repository = table.Column<string>(type: "TEXT", nullable: false),
                    PullRequestNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedHeadSha = table.Column<string>(type: "TEXT", nullable: false),
                    ReviewedHeadSha = table.Column<string>(type: "TEXT", nullable: false),
                    BaseSha = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorWorkerId = table.Column<string>(type: "TEXT", nullable: false),
                    ReviewerWorkerId = table.Column<string>(type: "TEXT", nullable: false),
                    ReviewerIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    RequiredChecksJson = table.Column<string>(type: "TEXT", nullable: false),
                    ObservationId = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AttemptedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    MergeCommitSha = table.Column<string>(type: "TEXT", nullable: true),
                    ReceiptJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubMergeIntents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GitHubCheckObservations_Repository_PullRequestNumber_HeadSha_ObservedAt",
                table: "GitHubCheckObservations",
                columns: new[] { "Repository", "PullRequestNumber", "HeadSha", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GitHubMergeIntents_Repository_PullRequestNumber_ExpectedHeadSha",
                table: "GitHubMergeIntents",
                columns: new[] { "Repository", "PullRequestNumber", "ExpectedHeadSha" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GitHubCheckObservations");

            migrationBuilder.DropTable(
                name: "GitHubMergeIntents");
        }
    }
}
