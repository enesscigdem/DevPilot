using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectBrain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "CodeChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspacePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    WorkspaceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProjectName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FilePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RelativePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Language = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SymbolName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TypeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MethodName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DeclaredSymbols = table.Column<string>(type: "text", nullable: false),
                    ChunkOrder = table.Column<int>(type: "integer", nullable: false),
                    StartLine = table.Column<int>(type: "integer", nullable: false),
                    EndLine = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(384)", nullable: true),
                    TokenCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IndexJobId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodeChunks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IndexJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspacePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    WorkspaceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalFiles = table.Column<int>(type: "integer", nullable: false),
                    ProcessedFiles = table.Column<int>(type: "integer", nullable: false),
                    TotalChunks = table.Column<int>(type: "integer", nullable: false),
                    ProcessedChunks = table.Column<int>(type: "integer", nullable: false),
                    ChunksEmbedded = table.Column<int>(type: "integer", nullable: false),
                    ChunksSkipped = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EmbeddingProviderStatus = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CodeChunks_ContentHash",
                table: "CodeChunks",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_CodeChunks_WorkspacePath_RelativePath_ChunkOrder",
                table: "CodeChunks",
                columns: new[] { "WorkspacePath", "RelativePath", "ChunkOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndexJobs_WorkspacePath",
                table: "IndexJobs",
                column: "WorkspacePath");

            migrationBuilder.CreateIndex(
                name: "IX_IndexJobs_WorkspacePath_StartedAt",
                table: "IndexJobs",
                columns: new[] { "WorkspacePath", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CodeChunks");

            migrationBuilder.DropTable(
                name: "IndexJobs");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
