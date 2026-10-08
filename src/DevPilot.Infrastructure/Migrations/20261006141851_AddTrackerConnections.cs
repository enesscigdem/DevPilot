using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackerConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExternalConnectionId",
                table: "DevelopmentTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalKey",
                table: "DevelopmentTasks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSource",
                table: "DevelopmentTasks",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalUrl",
                table: "DevelopmentTasks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TrackerConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EncryptedToken = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackerConnections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentTasks_RepositoryWorkspaceId_ExternalSource_Exter~",
                table: "DevelopmentTasks",
                columns: new[] { "RepositoryWorkspaceId", "ExternalSource", "ExternalKey" },
                unique: true,
                filter: "\"ExternalKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackerConnections");

            migrationBuilder.DropIndex(
                name: "IX_DevelopmentTasks_RepositoryWorkspaceId_ExternalSource_Exter~",
                table: "DevelopmentTasks");

            migrationBuilder.DropColumn(
                name: "ExternalConnectionId",
                table: "DevelopmentTasks");

            migrationBuilder.DropColumn(
                name: "ExternalKey",
                table: "DevelopmentTasks");

            migrationBuilder.DropColumn(
                name: "ExternalSource",
                table: "DevelopmentTasks");

            migrationBuilder.DropColumn(
                name: "ExternalUrl",
                table: "DevelopmentTasks");
        }
    }
}
