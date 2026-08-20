using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionLeaseAndCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LeaseToken",
                table: "TaskExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HeartbeatAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancellationRequestedAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "TaskExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "TaskExecutions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskExecutions_Status_LeaseExpiresAt",
                table: "TaskExecutions",
                columns: new[] { "Status", "LeaseExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskExecutions_Status_LeaseExpiresAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "LeaseToken",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "HeartbeatAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "TaskExecutions");
        }
    }
}
