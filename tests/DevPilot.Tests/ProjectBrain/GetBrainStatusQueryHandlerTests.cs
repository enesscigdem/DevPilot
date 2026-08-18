using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.ProjectBrain.Queries.GetBrainStatus;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Domain.ProjectBrain.Entities;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.ProjectBrain.Repositories;
using DevPilot.Infrastructure.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.ProjectBrain;

public sealed class GetBrainStatusQueryHandlerTests
{
    private readonly DevPilotDbContext _dbContext;
    private readonly IRepositoryWorkspaceQuery _workspaceQuery;
    private readonly ICodeChunkRepository _chunkRepository;
    private readonly IIndexJobRepository _jobRepository;
    private readonly GetBrainStatusQueryHandler _handler;

    public GetBrainStatusQueryHandlerTests()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase("GetBrainStatusTestDb_" + Guid.NewGuid().ToString("N"))
            .Options;

        _dbContext = new DevPilotDbContext(options);
        _workspaceQuery = new RepositoryWorkspaceQuery(_dbContext);
        _chunkRepository = new EfCodeChunkRepository(_dbContext);
        _jobRepository = new EfIndexJobRepository(_dbContext);

        _handler = new GetBrainStatusQueryHandler(
            _workspaceQuery,
            _chunkRepository,
            _jobRepository,
            NullLogger<GetBrainStatusQueryHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_WhenWorkspaceNotFound_ReturnsNotFound()
    {
        var result = await _handler.HandleAsync(new GetBrainStatusQuery(Guid.NewGuid()));
        result.NotFound.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenUnindexed_ReturnsUnindexedState()
    {
        var workspace = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "testowner",
            Repository = "testrepo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = "C:/fake/path",
        };
        _dbContext.RepositoryWorkspaces.Add(workspace);
        await _dbContext.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetBrainStatusQuery(workspace.Id));

        result.Success.Should().BeTrue();
        result.Status.Should().NotBeNull();
        result.Status!.State.Should().Be("unindexed");
        result.Status.TotalFiles.Should().Be(0);
        result.Status.TotalChunks.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WhenIndexed_ReportsHonestStatisticsAndSteps()
    {
        var workspace = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "testowner",
            Repository = "testrepo",
            Branch = "main",
            CommitSha = "commit123",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = "C:/fake/path",
        };
        _dbContext.RepositoryWorkspaces.Add(workspace);

        var chunk1 = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            ProjectName = "DevPilot.Api",
            RelativePath = "src/DevPilot.Api/Controllers/AuthController.cs",
            TypeName = "AuthController",
            SymbolName = "AuthController.Login",
            Content = "class AuthController {}",
            ContentHash = "h1",
        };

        var chunk2 = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            ProjectName = "DevPilot.Domain",
            RelativePath = "src/DevPilot.Domain/User.cs",
            TypeName = "User",
            SymbolName = "User.Id",
            Content = "class User {}",
            ContentHash = "h2",
        };

        _dbContext.CodeChunks.AddRange(chunk1, chunk2);

        var job = new IndexJob
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            CommitSha = "commit123",
            Status = IndexJobStatus.Completed,
            TotalFiles = 2,
            TotalChunks = 2,
            ChunksEmbedded = 0, // Honest: no embeddings generated
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-4),
        };
        _dbContext.IndexJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetBrainStatusQuery(workspace.Id));

        result.Success.Should().BeTrue();
        result.Status.Should().NotBeNull();
        result.Status!.State.Should().Be("ready");
        result.Status.TotalFiles.Should().Be(2);
        result.Status.TotalChunks.Should().Be(2);
        result.Status.TotalTypes.Should().Be(2);
        result.Status.TotalSymbols.Should().Be(2);

        // Honest steps: Embedding step is not done because 0 chunks embedded
        var embedStep = result.Status.Steps.FirstOrDefault(s => s.Label == "Index embeddings");
        embedStep.Should().NotBeNull();
        embedStep!.Done.Should().BeFalse();

        // Project source groups
        result.Status.SourceGroups.Should().HaveCount(2);
        var apiGroup = result.Status.SourceGroups.First(g => g.Project == "DevPilot.Api");
        apiGroup.Layer.Should().Be("Web");
        var domainGroup = result.Status.SourceGroups.First(g => g.Project == "DevPilot.Domain");
        domainGroup.Layer.Should().Be("Domain");
    }

    [Fact]
    public async Task HandleAsync_WhenCommitShaDiffers_ReportsStale()
    {
        var workspace = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "testowner",
            Repository = "testrepo",
            Branch = "main",
            CommitSha = "new_sha_456",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = "C:/fake/path",
        };
        _dbContext.RepositoryWorkspaces.Add(workspace);

        var chunk = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            ProjectName = "DevPilot.Api",
            RelativePath = "src/Test.cs",
            Content = "class Test {}",
            ContentHash = "h",
        };
        _dbContext.CodeChunks.Add(chunk);

        var job = new IndexJob
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            CommitSha = "old_sha_123",
            Status = IndexJobStatus.Completed,
            TotalFiles = 1,
            TotalChunks = 1,
            StartedAt = DateTime.UtcNow.AddHours(-1),
            CompletedAt = DateTime.UtcNow.AddHours(-1),
        };
        _dbContext.IndexJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetBrainStatusQuery(workspace.Id));

        result.Success.Should().BeTrue();
        result.Status!.State.Should().Be("stale");
    }

    [Fact]
    public async Task HandleAsync_AccuratelyReconcilesSymbolsAndSourceGroups_ScopedToWorkspace()
    {
        var targetWorkspace = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            CommitSha = "sha1",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = "C:/ws1",
        };
        var otherWorkspace = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "other",
            Repository = "repo",
            Branch = "main",
            CommitSha = "sha2",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = "C:/ws2",
        };
        _dbContext.RepositoryWorkspaces.AddRange(targetWorkspace, otherWorkspace);

        // Chunks for target workspace
        var chunk1 = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = targetWorkspace.Id,
            ProjectName = "DevPilot.Domain",
            RelativePath = "src/Domain/Entity.cs",
            TypeName = "Entity",
            DeclaredSymbols = "Entity, Id, Name, GetDisplayName",
            Content = "code",
            ContentHash = "h1",
        };
        var chunk2 = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = targetWorkspace.Id,
            ProjectName = "DevPilot.Domain",
            RelativePath = "src/Domain/ValueObject.cs",
            TypeName = "ValueObject",
            DeclaredSymbols = "ValueObject, Equals, GetHashCode",
            Content = "code",
            ContentHash = "h2",
        };
        var chunk3 = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = targetWorkspace.Id,
            ProjectName = "DevPilot.Application",
            RelativePath = "src/App/Handler.cs",
            TypeName = "Handler",
            DeclaredSymbols = "Handler, HandleAsync, Validate",
            Content = "code",
            ContentHash = "h3",
        };

        // Chunk for other workspace that should NOT be counted
        var otherChunk = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = otherWorkspace.Id,
            ProjectName = "OtherProject",
            RelativePath = "src/Other.cs",
            TypeName = "Other",
            DeclaredSymbols = "Other, OtherMethod",
            Content = "code",
            ContentHash = "h4",
        };

        _dbContext.CodeChunks.AddRange(chunk1, chunk2, chunk3, otherChunk);

        var job = new IndexJob
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = targetWorkspace.Id,
            CommitSha = "sha1",
            Status = IndexJobStatus.Completed,
            TotalFiles = 3,
            TotalChunks = 3,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        _dbContext.IndexJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetBrainStatusQuery(targetWorkspace.Id));

        result.Success.Should().BeTrue();
        result.Status.Should().NotBeNull();
        result.Status!.TotalFiles.Should().Be(3);
        result.Status.TotalChunks.Should().Be(3);
        result.Status.TotalTypes.Should().Be(3); // Entity, ValueObject, Handler

        // Symbols: Entity, Id, Name, GetDisplayName (4) + ValueObject, Equals, GetHashCode (3) + Handler, HandleAsync, Validate (3) = 10 distinct
        result.Status.TotalSymbols.Should().Be(10);

        // Source groups
        result.Status.SourceGroups.Should().HaveCount(2);
        var domainGroup = result.Status.SourceGroups.First(g => g.Project == "DevPilot.Domain");
        domainGroup.Files.Should().Be(2);
        domainGroup.Symbols.Should().Be(7); // 4 from chunk1 + 3 from chunk2

        var appGroup = result.Status.SourceGroups.First(g => g.Project == "DevPilot.Application");
        appGroup.Files.Should().Be(1);
        appGroup.Symbols.Should().Be(3); // Handler, HandleAsync, Validate
    }
}

