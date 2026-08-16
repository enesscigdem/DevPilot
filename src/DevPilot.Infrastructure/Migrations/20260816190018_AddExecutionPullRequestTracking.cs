using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionPullRequestTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CiLastSyncedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CiStatus",
                table: "TaskExecutions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<DateTime>(
                name: "PullRequestClosedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestIntegrityStatus",
                table: "TaskExecutions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<DateTime>(
                name: "PullRequestLastSyncAttemptAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PullRequestLastSyncedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PullRequestMergedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestRemoteState",
                table: "TaskExecutions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<Guid>(
                name: "PullRequestSyncAttemptId",
                table: "TaskExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PullRequestSyncClaimedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExecutionCiChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CheckType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Conclusion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionCiChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionCiChecks_TaskExecutions_TaskExecutionId",
                        column: x => x.TaskExecutionId,
                        principalTable: "TaskExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionCiChecks_TaskExecutionId_ExternalId_CheckType",
                table: "ExecutionCiChecks",
                columns: new[] { "TaskExecutionId", "ExternalId", "CheckType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExecutionCiChecks");

            migrationBuilder.DropColumn(
                name: "CiLastSyncedAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "CiStatus",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestClosedAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestIntegrityStatus",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestLastSyncAttemptAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestLastSyncedAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestMergedAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestRemoteState",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestSyncAttemptId",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestSyncClaimedAt",
                table: "TaskExecutions");
        }
    }
}
