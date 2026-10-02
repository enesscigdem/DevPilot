using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InitialBaseCommitSha",
                table: "TaskExecutions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastChangeRequest",
                table: "TaskExecutions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastChangeRequestAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastChangeRequestResult",
                table: "TaskExecutions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RevisionCount",
                table: "TaskExecutions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitialBaseCommitSha",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "LastChangeRequest",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "LastChangeRequestAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "LastChangeRequestResult",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "RevisionCount",
                table: "TaskExecutions");
        }
    }
}
