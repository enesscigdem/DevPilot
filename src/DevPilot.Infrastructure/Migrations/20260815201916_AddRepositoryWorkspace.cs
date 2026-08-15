using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepositoryWorkspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Repository = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LocalPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryWorkspaces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryWorkspaces_Owner_Repository_Branch",
                table: "RepositoryWorkspaces",
                columns: new[] { "Owner", "Repository", "Branch" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositoryWorkspaces");
        }
    }
}
