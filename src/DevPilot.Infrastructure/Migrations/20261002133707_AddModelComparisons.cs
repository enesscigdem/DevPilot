using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddModelComparisons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ModelComparisonRunId",
                table: "TaskExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PinnedAiModelId",
                table: "TaskExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PinnedAiModelName",
                table: "TaskExecutions",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ModelComparisons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DevelopmentTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelComparisons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelComparisons_DevelopmentTasks_DevelopmentTaskId",
                        column: x => x.DevelopmentTaskId,
                        principalTable: "DevelopmentTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ModelComparisonRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelComparisonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    AiModelConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelComparisonRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelComparisonRuns_ModelComparisons_ModelComparisonId",
                        column: x => x.ModelComparisonId,
                        principalTable: "ModelComparisons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskExecutions_ModelComparisonRunId",
                table: "TaskExecutions",
                column: "ModelComparisonRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelComparisonRuns_ModelComparisonId_Position",
                table: "ModelComparisonRuns",
                columns: new[] { "ModelComparisonId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelComparisons_DevelopmentTaskId",
                table: "ModelComparisons",
                column: "DevelopmentTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelComparisonRuns");

            migrationBuilder.DropTable(
                name: "ModelComparisons");

            migrationBuilder.DropIndex(
                name: "IX_TaskExecutions_ModelComparisonRunId",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "ModelComparisonRunId",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PinnedAiModelId",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "PinnedAiModelName",
                table: "TaskExecutions");
        }
    }
}
