using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryWorkspaceIdToProjectBrain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RepositoryWorkspaceId",
                table: "CodeChunks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RepositoryWorkspaceId",
                table: "IndexJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommitSha",
                table: "IndexJobs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql(@"
                UPDATE ""CodeChunks"" c
                SET ""RepositoryWorkspaceId"" = w.""Id""
                FROM ""RepositoryWorkspaces"" w
                WHERE c.""WorkspacePath"" = w.""LocalPath"" AND c.""RepositoryWorkspaceId"" IS NULL;

                UPDATE ""IndexJobs"" j
                SET ""RepositoryWorkspaceId"" = w.""Id""
                FROM ""RepositoryWorkspaces"" w
                WHERE j.""WorkspacePath"" = w.""LocalPath"" AND j.""RepositoryWorkspaceId"" IS NULL;
            ");

            migrationBuilder.CreateIndex(
                name: "IX_CodeChunks_RepositoryWorkspaceId",
                table: "CodeChunks",
                column: "RepositoryWorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_CodeChunks_RepositoryWorkspaceId_RelativePath_ChunkOrder",
                table: "CodeChunks",
                columns: new[] { "RepositoryWorkspaceId", "RelativePath", "ChunkOrder" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CodeChunks_RepositoryWorkspaces_RepositoryWorkspaceId",
                table: "CodeChunks",
                column: "RepositoryWorkspaceId",
                principalTable: "RepositoryWorkspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.CreateIndex(
                name: "IX_IndexJobs_RepositoryWorkspaceId",
                table: "IndexJobs",
                column: "RepositoryWorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_IndexJobs_RepositoryWorkspaceId_StartedAt",
                table: "IndexJobs",
                columns: new[] { "RepositoryWorkspaceId", "StartedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_IndexJobs_RepositoryWorkspaces_RepositoryWorkspaceId",
                table: "IndexJobs",
                column: "RepositoryWorkspaceId",
                principalTable: "RepositoryWorkspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CodeChunks_RepositoryWorkspaces_RepositoryWorkspaceId",
                table: "CodeChunks");

            migrationBuilder.DropForeignKey(
                name: "FK_IndexJobs_RepositoryWorkspaces_RepositoryWorkspaceId",
                table: "IndexJobs");

            migrationBuilder.DropIndex(
                name: "IX_CodeChunks_RepositoryWorkspaceId",
                table: "CodeChunks");

            migrationBuilder.DropIndex(
                name: "IX_CodeChunks_RepositoryWorkspaceId_RelativePath_ChunkOrder",
                table: "CodeChunks");

            migrationBuilder.DropIndex(
                name: "IX_IndexJobs_RepositoryWorkspaceId",
                table: "IndexJobs");

            migrationBuilder.DropIndex(
                name: "IX_IndexJobs_RepositoryWorkspaceId_StartedAt",
                table: "IndexJobs");

            migrationBuilder.DropColumn(
                name: "RepositoryWorkspaceId",
                table: "CodeChunks");

            migrationBuilder.DropColumn(
                name: "RepositoryWorkspaceId",
                table: "IndexJobs");

            migrationBuilder.DropColumn(
                name: "CommitSha",
                table: "IndexJobs");
        }
    }
}
