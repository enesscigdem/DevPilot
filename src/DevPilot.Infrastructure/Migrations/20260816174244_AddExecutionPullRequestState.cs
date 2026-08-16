using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionPullRequestState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PullRequestAttemptId",
                table: "TaskExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestBaseBranch",
                table: "TaskExecutions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PullRequestClaimedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PullRequestCreatedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PullRequestNumber",
                table: "TaskExecutions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestStatus",
                table: "TaskExecutions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "PullRequestUrl",
                table: "TaskExecutions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PullRequestAttemptId",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestBaseBranch",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestClaimedAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestCreatedAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestNumber",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestStatus",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PullRequestUrl",
                table: "TaskExecutions");
        }
    }
}
