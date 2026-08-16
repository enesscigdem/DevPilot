using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskExecutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DevelopmentTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskExecutions_DevelopmentTasks_DevelopmentTaskId",
                        column: x => x.DevelopmentTaskId,
                        principalTable: "DevelopmentTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Composite index for status-filtered queries (e.g. HasActiveExecutionForTaskAsync).
            migrationBuilder.CreateIndex(
                name: "IX_TaskExecutions_DevelopmentTaskId_Status",
                table: "TaskExecutions",
                columns: new[] { "DevelopmentTaskId", "Status" });

            // Unique partial index — the authoritative DB-level guard against concurrent
            // duplicate active executions for the same task.
            migrationBuilder.CreateIndex(
                name: "IX_TaskExecutions_ActivePerTask",
                table: "TaskExecutions",
                column: "DevelopmentTaskId",
                unique: true,
                filter: "\"Status\" IN ('Pending', 'Running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskExecutions");
        }
    }
}
