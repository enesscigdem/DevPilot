using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGoals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Goals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryWorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PlanSource = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EstimatedInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedOutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedUsd = table.Column<decimal>(type: "numeric", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Goals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Goals_RepositoryWorkspaces_RepositoryWorkspaceId",
                        column: x => x.RepositoryWorkspaceId,
                        principalTable: "RepositoryWorkspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GoalTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GoalId = table.Column<Guid>(type: "uuid", nullable: false),
                    DevelopmentTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Wave = table.Column<int>(type: "integer", nullable: false),
                    Size = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Areas = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    DependsOn = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    BlockedBy = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    AnalysisAttempts = table.Column<int>(type: "integer", nullable: false),
                    AnalysisRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoalTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoalTasks_DevelopmentTasks_DevelopmentTaskId",
                        column: x => x.DevelopmentTaskId,
                        principalTable: "DevelopmentTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GoalTasks_Goals_GoalId",
                        column: x => x.GoalId,
                        principalTable: "Goals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Goals_RepositoryWorkspaceId_CreatedAt",
                table: "Goals",
                columns: new[] { "RepositoryWorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Goals_Status",
                table: "Goals",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_GoalTasks_DevelopmentTaskId",
                table: "GoalTasks",
                column: "DevelopmentTaskId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GoalTasks_GoalId_Key",
                table: "GoalTasks",
                columns: new[] { "GoalId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoalTasks");

            migrationBuilder.DropTable(
                name: "Goals");
        }
    }
}
