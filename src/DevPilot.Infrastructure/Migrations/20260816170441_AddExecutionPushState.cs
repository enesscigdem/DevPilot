using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionPushState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PushAttemptId",
                table: "TaskExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PushClaimedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PushStatus",
                table: "TaskExecutions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<DateTime>(
                name: "PushedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteBranchName",
                table: "TaskExecutions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteCommitSha",
                table: "TaskExecutions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PushAttemptId",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PushClaimedAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PushStatus",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PushedAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "RemoteBranchName",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "RemoteCommitSha",
                table: "TaskExecutions");
        }
    }
}
