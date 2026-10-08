using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGitProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RepositoryWorkspaces_Owner_Repository_Branch",
                table: "RepositoryWorkspaces");

            migrationBuilder.AddColumn<Guid>(
                name: "GitConnectionId",
                table: "RepositoryWorkspaces",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Host",
                table: "RepositoryWorkspaces",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "github.com");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "RepositoryWorkspaces",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "GitHub");

            migrationBuilder.CreateTable(
                name: "GitConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EncryptedToken = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitConnections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryWorkspaces_GitConnectionId",
                table: "RepositoryWorkspaces",
                column: "GitConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryWorkspaces_Host_Owner_Repository_Branch",
                table: "RepositoryWorkspaces",
                columns: new[] { "Host", "Owner", "Repository", "Branch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitConnections_Provider_Host",
                table: "GitConnections",
                columns: new[] { "Provider", "Host" });

            migrationBuilder.AddForeignKey(
                name: "FK_RepositoryWorkspaces_GitConnections_GitConnectionId",
                table: "RepositoryWorkspaces",
                column: "GitConnectionId",
                principalTable: "GitConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RepositoryWorkspaces_GitConnections_GitConnectionId",
                table: "RepositoryWorkspaces");

            migrationBuilder.DropTable(
                name: "GitConnections");

            migrationBuilder.DropIndex(
                name: "IX_RepositoryWorkspaces_GitConnectionId",
                table: "RepositoryWorkspaces");

            migrationBuilder.DropIndex(
                name: "IX_RepositoryWorkspaces_Host_Owner_Repository_Branch",
                table: "RepositoryWorkspaces");

            migrationBuilder.DropColumn(
                name: "GitConnectionId",
                table: "RepositoryWorkspaces");

            migrationBuilder.DropColumn(
                name: "Host",
                table: "RepositoryWorkspaces");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "RepositoryWorkspaces");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryWorkspaces_Owner_Repository_Branch",
                table: "RepositoryWorkspaces",
                columns: new[] { "Owner", "Repository", "Branch" },
                unique: true);
        }
    }
}
