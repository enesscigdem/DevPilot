using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutomationPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryWorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Paused = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MaxFilesChanged = table.Column<int>(type: "integer", nullable: false),
                    MaxLinesChanged = table.Column<int>(type: "integer", nullable: false),
                    MaxParallelExecutions = table.Column<int>(type: "integer", nullable: false),
                    ProtectedPaths = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    RequireGreenCiForMerge = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationPolicies_RepositoryWorkspaces_RepositoryWorkspace~",
                        column: x => x.RepositoryWorkspaceId,
                        principalTable: "RepositoryWorkspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationPolicies_RepositoryWorkspaceId",
                table: "AutomationPolicies",
                column: "RepositoryWorkspaceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationPolicies");
        }
    }
}
