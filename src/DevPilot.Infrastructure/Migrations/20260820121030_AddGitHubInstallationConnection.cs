using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubInstallationConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GitHubInstallationConnectionId",
                table: "RepositoryWorkspaces",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPrivate",
                table: "RepositoryWorkspaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RemoteUrl",
                table: "RepositoryWorkspaces",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GitHubInstallationConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalInstallationId = table.Column<long>(type: "bigint", nullable: false),
                    AccountLogin = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AccountType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TargetId = table.Column<long>(type: "bigint", nullable: false),
                    TargetAvatarUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastVerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubInstallationConnections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryWorkspaces_GitHubInstallationConnectionId",
                table: "RepositoryWorkspaces",
                column: "GitHubInstallationConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_GitHubInstallationConnections_AccountLogin",
                table: "GitHubInstallationConnections",
                column: "AccountLogin");

            migrationBuilder.CreateIndex(
                name: "IX_GitHubInstallationConnections_ExternalInstallationId",
                table: "GitHubInstallationConnections",
                column: "ExternalInstallationId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_RepositoryWorkspaces_GitHubInstallationConnections_GitHubIn~",
                table: "RepositoryWorkspaces",
                column: "GitHubInstallationConnectionId",
                principalTable: "GitHubInstallationConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RepositoryWorkspaces_GitHubInstallationConnections_GitHubIn~",
                table: "RepositoryWorkspaces");

            migrationBuilder.DropTable(
                name: "GitHubInstallationConnections");

            migrationBuilder.DropIndex(
                name: "IX_RepositoryWorkspaces_GitHubInstallationConnectionId",
                table: "RepositoryWorkspaces");

            migrationBuilder.DropColumn(
                name: "GitHubInstallationConnectionId",
                table: "RepositoryWorkspaces");

            migrationBuilder.DropColumn(
                name: "IsPrivate",
                table: "RepositoryWorkspaces");

            migrationBuilder.DropColumn(
                name: "RemoteUrl",
                table: "RepositoryWorkspaces");
        }
    }
}
