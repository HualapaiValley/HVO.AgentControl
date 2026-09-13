using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Assignments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerId = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    TemplateVersion = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Commands",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerId = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Origin = table.Column<string>(type: "TEXT", nullable: false),
                    NativeMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    ResultId = table.Column<string>(type: "TEXT", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    QueueOrder = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Id = table.Column<string>(type: "TEXT", nullable: true),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: true),
                    WorkerId = table.Column<string>(type: "TEXT", nullable: true),
                    CommandId = table.Column<string>(type: "TEXT", nullable: true),
                    NativeId = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Provenance = table.Column<string>(type: "TEXT", nullable: false),
                    Generation = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkerId = table.Column<string>(type: "TEXT", nullable: false),
                    NativeId = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    NativeCreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Requests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerId = table.Column<string>(type: "TEXT", nullable: false),
                    NativeId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    ReplyCommandId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Runtimes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    HostKeySha256 = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialReference = table.Column<string>(type: "TEXT", nullable: false),
                    Authentication = table.Column<string>(type: "TEXT", nullable: false),
                    PassphraseReference = table.Column<string>(type: "TEXT", nullable: true),
                    ServerPasswordReference = table.Column<string>(type: "TEXT", nullable: false),
                    StateDirectory = table.Column<string>(type: "TEXT", nullable: false),
                    AllowedRoots = table.Column<string>(type: "TEXT", nullable: false),
                    Executable = table.Column<string>(type: "TEXT", nullable: false),
                    ApiPort = table.Column<int>(type: "INTEGER", nullable: false),
                    InstallIfMissing = table.Column<bool>(type: "INTEGER", nullable: false),
                    Capacity = table.Column<int>(type: "INTEGER", nullable: false),
                    Labels = table.Column<string>(type: "TEXT", nullable: false),
                    DesiredConnected = table.Column<bool>(type: "INTEGER", nullable: false),
                    ManagedServerId = table.Column<string>(type: "TEXT", nullable: false),
                    Transport = table.Column<string>(type: "TEXT", nullable: false),
                    Health = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderState = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    Diagnostic = table.Column<string>(type: "TEXT", nullable: false),
                    ModelsJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastHealthyAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastEventAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Generation = table.Column<int>(type: "INTEGER", nullable: false),
                    ReconnectAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runtimes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    ManagedServerId = table.Column<string>(type: "TEXT", nullable: false),
                    NativeSessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Project = table.Column<string>(type: "TEXT", nullable: false),
                    Directory = table.Column<string>(type: "TEXT", nullable: false),
                    Branch = table.Column<string>(type: "TEXT", nullable: false),
                    BaseRef = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Activity = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentAction = table.Column<string>(type: "TEXT", nullable: false),
                    Stale = table.Column<bool>(type: "INTEGER", nullable: false),
                    HistoryGap = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastObservedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastModelAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastToolAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastStatusInquiryAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Commands_State_QueueOrder",
                table: "Commands",
                columns: new[] { "State", "QueueOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_WorkerId_Sequence",
                table: "Events",
                columns: new[] { "WorkerId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_WorkerId_NativeId",
                table: "Messages",
                columns: new[] { "WorkerId", "NativeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Requests_WorkerId_Kind_NativeId",
                table: "Requests",
                columns: new[] { "WorkerId", "Kind", "NativeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workers_RuntimeId_Directory",
                table: "Workers",
                columns: new[] { "RuntimeId", "Directory" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workers_RuntimeId_ManagedServerId_NativeSessionId",
                table: "Workers",
                columns: new[] { "RuntimeId", "ManagedServerId", "NativeSessionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Assignments");

            migrationBuilder.DropTable(
                name: "Commands");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Requests");

            migrationBuilder.DropTable(
                name: "Runtimes");

            migrationBuilder.DropTable(
                name: "Workers");
        }
    }
}
