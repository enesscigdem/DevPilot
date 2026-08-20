using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectBrainConversationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectBrainConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryWorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectBrainConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectBrainConversations_RepositoryWorkspaces_RepositoryWo~",
                        column: x => x.RepositoryWorkspaceId,
                        principalTable: "RepositoryWorkspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectBrainMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<int>(type: "integer", nullable: true),
                    Elapsed = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CitationsJson = table.Column<string>(type: "jsonb", nullable: true),
                    ContextFilesJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectBrainMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectBrainMessages_ProjectBrainConversations_Conversation~",
                        column: x => x.ConversationId,
                        principalTable: "ProjectBrainConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectBrainConversations_RepositoryWorkspaceId",
                table: "ProjectBrainConversations",
                column: "RepositoryWorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectBrainConversations_RepositoryWorkspaceId_UpdatedAt",
                table: "ProjectBrainConversations",
                columns: new[] { "RepositoryWorkspaceId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectBrainMessages_ConversationId",
                table: "ProjectBrainMessages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectBrainMessages_ConversationId_CreatedAt",
                table: "ProjectBrainMessages",
                columns: new[] { "ConversationId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectBrainMessages");

            migrationBuilder.DropTable(
                name: "ProjectBrainConversations");
        }
    }
}
