using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDevelopmentTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DevelopmentTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryWorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    AcceptanceCriteria = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Priority = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevelopmentTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevelopmentTasks_RepositoryWorkspaces_RepositoryWorkspaceId",
                        column: x => x.RepositoryWorkspaceId,
                        principalTable: "RepositoryWorkspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentTasks_RepositoryWorkspaceId",
                table: "DevelopmentTasks",
                column: "RepositoryWorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentTasks_RepositoryWorkspaceId_Priority",
                table: "DevelopmentTasks",
                columns: new[] { "RepositoryWorkspaceId", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentTasks_RepositoryWorkspaceId_Status",
                table: "DevelopmentTasks",
                columns: new[] { "RepositoryWorkspaceId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DevelopmentTasks");
        }
    }
}
