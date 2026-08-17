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
            // No-op for compatibility: TaskExecutions table creation is owned by 20260816101000_CreateTaskExecutionsFoundation.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op for compatibility: TaskExecutions table drop is owned by 20260816101000_CreateTaskExecutionsFoundation.
        }
    }
}
