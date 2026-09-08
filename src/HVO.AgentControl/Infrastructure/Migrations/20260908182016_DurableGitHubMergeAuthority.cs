using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableGitHubMergeAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GitHubCheckObservations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    IntentId = table.Column<string>(type: "TEXT", nullable: false),
                    Repository = table.Column<string>(type: "TEXT", nullable: false),
                    PullRequestNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    HeadSha = table.Column<string>(type: "TEXT", nullable: false),
                    BaseBranch = table.Column<string>(type: "TEXT", nullable: false),
                    BaseSha = table.Column<string>(type: "TEXT", nullable: false),
                    PullRequestState = table.Column<string>(type: "TEXT", nullable: false),
                    Mergeable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Merged = table.Column<bool>(type: "INTEGER", nullable: false),
                    MergeCommitSha = table.Column<string>(type: "TEXT", nullable: true),
                    AuthorIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    ReviewsJson = table.Column<string>(type: "TEXT", nullable: false),
                    UnresolvedReviewThreads = table.Column<int>(type: "INTEGER", nullable: false),
                    ChecksJson = table.Column<string>(type: "TEXT", nullable: false),
                    StatusesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Complete = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubCheckObservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitHubMergeAttempts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    IntentId = table.Column<string>(type: "TEXT", nullable: false),
                    Repository = table.Column<string>(type: "TEXT", nullable: false),
                    BaseBranch = table.Column<string>(type: "TEXT", nullable: false),
                    HeadSha = table.Column<string>(type: "TEXT", nullable: false),
                    ObservationId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    MergeCommitSha = table.Column<string>(type: "TEXT", nullable: true),
                    ReceiptJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubMergeAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitHubMergeIntents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Repository = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    PullRequestNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedHeadSha = table.Column<string>(type: "TEXT", nullable: false),
                    BaseBranch = table.Column<string>(type: "TEXT", nullable: false),
                    BaseSha = table.Column<string>(type: "TEXT", nullable: false),
                    ReviewReceiptId = table.Column<string>(type: "TEXT", nullable: false),
                    PolicyId = table.Column<string>(type: "TEXT", nullable: false),
                    PolicyRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    RequiredChecksJson = table.Column<string>(type: "TEXT", nullable: false),
                    ObservationId = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    MergeCommitSha = table.Column<string>(type: "TEXT", nullable: true),
                    ReceiptJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubMergeIntents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitHubMergeLeases",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Repository = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    BaseBranch = table.Column<string>(type: "TEXT", nullable: false),
                    IntentId = table.Column<string>(type: "TEXT", nullable: false),
                    AcquiredAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubMergeLeases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitHubMergePolicies",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Repository = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    BaseBranch = table.Column<string>(type: "TEXT", nullable: false),
                    RequiredChecksJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubMergePolicies", x => new { x.Id, x.Revision });
                });

            migrationBuilder.CreateTable(
                name: "GitHubReviewReceipts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Repository = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    PullRequestNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    HeadSha = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorWorkerId = table.Column<string>(type: "TEXT", nullable: false),
                    ReviewerWorkerId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorCommandId = table.Column<string>(type: "TEXT", nullable: false),
                    ReviewCommandId = table.Column<string>(type: "TEXT", nullable: false),
                    GitHubReviewId = table.Column<long>(type: "INTEGER", nullable: false),
                    GitHubReviewerIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubReviewReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GitHubCheckObservations_Repository_PullRequestNumber_HeadSha_ObservedAt",
                table: "GitHubCheckObservations",
                columns: new[] { "Repository", "PullRequestNumber", "HeadSha", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GitHubMergeAttempts_IntentId_State",
                table: "GitHubMergeAttempts",
                columns: new[] { "IntentId", "State" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitHubMergeIntents_Repository_PullRequestNumber_ExpectedHeadSha",
                table: "GitHubMergeIntents",
                columns: new[] { "Repository", "PullRequestNumber", "ExpectedHeadSha" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitHubMergeLeases_Repository_BaseBranch",
                table: "GitHubMergeLeases",
                columns: new[] { "Repository", "BaseBranch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitHubMergePolicies_Repository_BaseBranch_Revision",
                table: "GitHubMergePolicies",
                columns: new[] { "Repository", "BaseBranch", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitHubReviewReceipts_Repository_PullRequestNumber_HeadSha_ReviewerWorkerId",
                table: "GitHubReviewReceipts",
                columns: new[] { "Repository", "PullRequestNumber", "HeadSha", "ReviewerWorkerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GitHubCheckObservations");

            migrationBuilder.DropTable(
                name: "GitHubMergeAttempts");

            migrationBuilder.DropTable(
                name: "GitHubMergeIntents");

            migrationBuilder.DropTable(
                name: "GitHubMergeLeases");

            migrationBuilder.DropTable(
                name: "GitHubMergePolicies");

            migrationBuilder.DropTable(
                name: "GitHubReviewReceipts");
        }
    }
}
