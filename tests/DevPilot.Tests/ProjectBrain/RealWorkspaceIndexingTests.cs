using System;
using System.IO;
using System.Threading.Tasks;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.ProjectBrain.Commands.IndexWorkspace;
using DevPilot.Application.ProjectBrain.Models;
using DevPilot.Application.ProjectBrain.Queries.GetBrainStatus;
using DevPilot.Domain.ProjectBrain.Entities;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.CodeAnalysis;
using DevPilot.Infrastructure.ProjectBrain;
using DevPilot.Infrastructure.ProjectBrain.EmbeddingProviders;
using DevPilot.Infrastructure.ProjectBrain.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.ProjectBrain;

public sealed class RealWorkspaceIndexingTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public RealWorkspaceIndexingTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task IndexRealDevPilotWorkspace_ExtractsChunksAndSymbolsWithoutError()
    {
        var workspacePath = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(workspacePath, "DevPilot.sln")) && Directory.GetParent(workspacePath) != null)
        {
            workspacePath = Directory.GetParent(workspacePath)!.FullName;
        }

        Directory.Exists(workspacePath).Should().BeTrue();
        File.Exists(Path.Combine(workspacePath, "DevPilot.sln")).Should().BeTrue();

        var analyzer = new RoslynRepositoryAnalyzer(NullLogger<RoslynRepositoryAnalyzer>.Instance);
        var analysisResult = await analyzer.AnalyzeAsync(new RepositoryAnalysisRequest
        {
            WorkspacePath = workspacePath
        });

        analysisResult.Should().NotBeNull();

        var chunker = new RepositoryChunker(NullLogger<RepositoryChunker>.Instance);
        var chunks = await chunker.ChunkRepositoryAsync(new ChunkMetadata
        {
            WorkspacePath = workspacePath,
            WorkspaceName = "enesscigdem/DevPilot",
            RoslynAnalysis = analysisResult
        }, analysisResult);

        chunks.Should().NotBeEmpty();

        // Validate all chunks respect column length bounds
        foreach (var chunk in chunks)
        {
            chunk.WorkspacePath.Length.Should().BeLessThanOrEqualTo(500);
            chunk.WorkspaceName.Length.Should().BeLessThanOrEqualTo(200);
            chunk.ProjectName.Length.Should().BeLessThanOrEqualTo(200);
            chunk.FilePath.Length.Should().BeLessThanOrEqualTo(500);
            chunk.RelativePath.Length.Should().BeLessThanOrEqualTo(500);
            chunk.Language.Length.Should().BeLessThanOrEqualTo(50);
            chunk.SymbolName?.Length.Should().BeLessThanOrEqualTo(200);
            chunk.TypeName?.Length.Should().BeLessThanOrEqualTo(200);
            chunk.MethodName?.Length.Should().BeLessThanOrEqualTo(200);
            chunk.ContentHash.Length.Should().Be(64);
        }

        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase("RealWorkspaceIndexingDb_" + Guid.NewGuid().ToString("N"))
            .Options;

        using var dbContext = new DevPilotDbContext(options);
        var chunkRepo = new EfCodeChunkRepository(dbContext);
        var jobRepo = new EfIndexJobRepository(dbContext);
        var handler = new IndexWorkspaceCommandHandler(
            chunker,
            chunkRepo,
            jobRepo,
            new NullEmbeddingProvider(),
            NullLogger<IndexWorkspaceCommandHandler>.Instance);

        var workspaceId = Guid.NewGuid();
        var command = new IndexWorkspaceCommand(
            WorkspacePath: workspacePath,
            WorkspaceName: "enesscigdem/DevPilot",
            AnalysisResult: analysisResult,
            RepositoryWorkspaceId: workspaceId);

        var result = await handler.HandleAsync(command);

        result.Success.Should().BeTrue();
        result.FilesIndexed.Should().BeGreaterThan(10);
        result.ChunksIndexed.Should().BeGreaterThan(20);

        var statusHandler = new GetBrainStatusQueryHandler(
            new DevPilot.Infrastructure.Tasks.RepositoryWorkspaceQuery(dbContext),
            chunkRepo,
            jobRepo,
            NullLogger<GetBrainStatusQueryHandler>.Instance);

        var ws = new DevPilot.Domain.Entities.RepositoryWorkspace
        {
            Id = workspaceId,
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "main",
            CommitSha = "local",
            Status = DevPilot.Domain.Enums.RepositoryWorkspaceStatus.Completed,
            LocalPath = workspacePath
        };
        dbContext.RepositoryWorkspaces.Add(ws);
        await dbContext.SaveChangesAsync();

        var statusResult = await statusHandler.HandleAsync(new DevPilot.Application.ProjectBrain.Queries.GetBrainStatus.GetBrainStatusQuery(workspaceId));
        statusResult.Success.Should().BeTrue();
        statusResult.Status.Should().NotBeNull();
        var s = statusResult.Status!;

        _output.WriteLine($"STATUS STATS: TotalFiles={s.TotalFiles}, TotalChunks={s.TotalChunks}, TotalTypes={s.TotalTypes}, TotalSymbols={s.TotalSymbols}");
        foreach (var g in s.SourceGroups)
        {
            _output.WriteLine($"SOURCE GROUP: Project={g.Project}, Layer={g.Layer}, Files={g.Files}, Symbols={g.Symbols}");
        }
    }
}

