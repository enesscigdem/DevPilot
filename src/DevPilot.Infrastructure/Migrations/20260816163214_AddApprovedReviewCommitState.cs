using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovedReviewCommitState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovedChangeFingerprint",
                table: "TaskExecutions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseCommitSha",
                table: "TaskExecutions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CommitAttemptId",
                table: "TaskExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CommitClaimedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommitSha",
                table: "TaskExecutions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommitStatus",
                table: "TaskExecutions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<DateTime>(
                name: "CommittedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedChangeFingerprint",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "BaseCommitSha",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "CommitAttemptId",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "CommitClaimedAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "CommitSha",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "CommitStatus",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "CommittedAt",
                table: "TaskExecutions");
        }
    }
}
