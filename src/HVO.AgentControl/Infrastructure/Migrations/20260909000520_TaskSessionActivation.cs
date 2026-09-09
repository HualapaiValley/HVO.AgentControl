using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.AgentControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TaskSessionActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerWorkerSlotId",
                table: "WorkItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerWorkerSlotId",
                table: "WorkItemPhases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreationCommandId",
                table: "TaskSessionBindings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "TaskSessionBindings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "WorkerId",
                table: "TaskSessionBindings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_OwnerWorkerSlotId",
                table: "WorkItems",
                column: "OwnerWorkerSlotId",
                unique: true,
                filter: "OwnerWorkerSlotId IS NOT NULL AND State NOT IN ('Released', 'Abandoned')");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemPhases_OwnerWorkerSlotId",
                table: "WorkItemPhases",
                column: "OwnerWorkerSlotId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskSessionBindings_CreationCommandId",
                table: "TaskSessionBindings",
                column: "CreationCommandId",
                unique: true,
                filter: "CreationCommandId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskSessionBindings_WorkerId",
                table: "TaskSessionBindings",
                column: "WorkerId",
                unique: true,
                filter: "WorkerId IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskSessionBindings_Workers_WorkerId",
                table: "TaskSessionBindings",
                column: "WorkerId",
                principalTable: "Workers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkItemPhases_WorkerSlots_OwnerWorkerSlotId",
                table: "WorkItemPhases",
                column: "OwnerWorkerSlotId",
                principalTable: "WorkerSlots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkItems_WorkerSlots_OwnerWorkerSlotId",
                table: "WorkItems",
                column: "OwnerWorkerSlotId",
                principalTable: "WorkerSlots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskSessionBindings_Workers_WorkerId",
                table: "TaskSessionBindings");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkItemPhases_WorkerSlots_OwnerWorkerSlotId",
                table: "WorkItemPhases");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkItems_WorkerSlots_OwnerWorkerSlotId",
                table: "WorkItems");

            migrationBuilder.DropIndex(
                name: "IX_WorkItems_OwnerWorkerSlotId",
                table: "WorkItems");

            migrationBuilder.DropIndex(
                name: "IX_WorkItemPhases_OwnerWorkerSlotId",
                table: "WorkItemPhases");

            migrationBuilder.DropIndex(
                name: "IX_TaskSessionBindings_CreationCommandId",
                table: "TaskSessionBindings");

            migrationBuilder.DropIndex(
                name: "IX_TaskSessionBindings_WorkerId",
                table: "TaskSessionBindings");

            migrationBuilder.DropColumn(
                name: "OwnerWorkerSlotId",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "OwnerWorkerSlotId",
                table: "WorkItemPhases");

            migrationBuilder.DropColumn(
                name: "CreationCommandId",
                table: "TaskSessionBindings");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "TaskSessionBindings");

            migrationBuilder.DropColumn(
                name: "WorkerId",
                table: "TaskSessionBindings");
        }
    }
}
