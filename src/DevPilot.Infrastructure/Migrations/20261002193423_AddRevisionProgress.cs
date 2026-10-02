using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRevisionProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChangeRequestCount",
                table: "TaskExecutions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "InitialRunCompletedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevisionBaseSnapshotSha",
                table: "TaskExecutions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevisionResultSnapshotSha",
                table: "TaskExecutions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChangeRequestCount",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "InitialRunCompletedAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "RevisionBaseSnapshotSha",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "RevisionResultSnapshotSha",
                table: "TaskExecutions");
        }
    }
}
