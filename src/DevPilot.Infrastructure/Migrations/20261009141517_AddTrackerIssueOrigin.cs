using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackerIssueOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DevelopmentTasks_RepositoryWorkspaceId_ExternalSource_Exter~",
                table: "DevelopmentTasks");

            migrationBuilder.AddColumn<string>(
                name: "ExternalOrigin",
                table: "DevelopmentTasks",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            // Tasks imported before this column existed belong to the site of the connection they were imported with.
            // Same form as TrackerOrigin.Normalize: trimmed, no trailing slash, lower case. Tasks whose connection was removed keep ''.
            migrationBuilder.Sql(
                "UPDATE \"DevelopmentTasks\" AS t SET \"ExternalOrigin\" = lower(rtrim(btrim(c.\"BaseUrl\"), '/')) " +
                "FROM \"TrackerConnections\" AS c WHERE t.\"ExternalConnectionId\" = c.\"Id\" AND t.\"ExternalKey\" IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentTasks_RepositoryWorkspaceId_ExternalSource_Exter~",
                table: "DevelopmentTasks",
                columns: new[] { "RepositoryWorkspaceId", "ExternalSource", "ExternalOrigin", "ExternalKey" },
                unique: true,
                filter: "\"ExternalKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DevelopmentTasks_RepositoryWorkspaceId_ExternalSource_Exter~",
                table: "DevelopmentTasks");

            migrationBuilder.DropColumn(
                name: "ExternalOrigin",
                table: "DevelopmentTasks");

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentTasks_RepositoryWorkspaceId_ExternalSource_Exter~",
                table: "DevelopmentTasks",
                columns: new[] { "RepositoryWorkspaceId", "ExternalSource", "ExternalKey" },
                unique: true,
                filter: "\"ExternalKey\" IS NOT NULL");
        }
    }
}
