using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionMergeState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MergeAttemptId",
                table: "TaskExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MergeClaimedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeCommitSha",
                table: "TaskExecutions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeMethod",
                table: "TaskExecutions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeStatus",
                table: "TaskExecutions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<DateTime>(
                name: "MergedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MergeAttemptId",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "MergeClaimedAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "MergeCommitSha",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "MergeMethod",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "MergeStatus",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "MergedAt",
                table: "TaskExecutions");
        }
    }
}
