using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveImpactAnalysisUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TaskImpactAnalyses_ActivePerTask",
                table: "TaskImpactAnalyses",
                column: "DevelopmentTaskId",
                unique: true,
                filter: "\"Status\" = 'InProgress'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskImpactAnalyses_ActivePerTask",
                table: "TaskImpactAnalyses");
        }
    }
}
