using DevPilot.Application.AiProviders;
using DevPilot.Application.ProjectBrain.Commands.AskBrain;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.ProjectBrain.Queries.SemanticSearch;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ProjectBrain.Entities;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.ProjectBrain.Repositories;
using DevPilot.Infrastructure.ProjectBrain.SemanticSearch;
using DevPilot.Infrastructure.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.ProjectBrain;

public sealed class AskBrainCommandHandlerTests
{
    private readonly DevPilotDbContext _dbContext;
    private readonly IRepositoryWorkspaceQuery _workspaceQuery;
    private readonly ICodeChunkRepository _chunkRepository;
    private readonly IIndexJobRepository _jobRepository;
    private readonly ISemanticSearchService _searchService;
    private readonly ISemanticSearchQueryHandler _searchHandler;
    private readonly FakeAiProvider _aiProvider;
    private readonly AskBrainCommandHandler _handler;

    public AskBrainCommandHandlerTests()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase("AskBrainTestDb_" + Guid.NewGuid().ToString("N"))
            .Options;

        _dbContext = new DevPilotDbContext(options);
        _workspaceQuery = new RepositoryWorkspaceQuery(_dbContext);
        _chunkRepository = new EfCodeChunkRepository(_dbContext);
        _jobRepository = new EfIndexJobRepository(_dbContext);
        _searchService = new EfSemanticSearchService(_dbContext, NullLogger<EfSemanticSearchService>.Instance);
        _searchHandler = new SemanticSearchQueryHandler(
            new DevPilot.Infrastructure.ProjectBrain.EmbeddingProviders.NullEmbeddingProvider(),
            _searchService,
            NullLogger<SemanticSearchQueryHandler>.Instance);
        _aiProvider = new FakeAiProvider();

        _handler = new AskBrainCommandHandler(
            _workspaceQuery,
            _chunkRepository,
            _jobRepository,
            _searchHandler,
            _aiProvider,
            NullLogger<AskBrainCommandHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_WhenWorkspaceNotFound_ReturnsFailure()
    {
        var result = await _handler.HandleAsync(new AskBrainCommand(Guid.NewGuid(), "How does auth work?"));
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task HandleAsync_WhenWorkspaceUnindexed_ReturnsIsUnindexedTrue()
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

        var result = await _handler.HandleAsync(new AskBrainCommand(workspace.Id, "How does auth work?"));
        result.Success.Should().BeFalse();
        result.IsUnindexed.Should().BeTrue();
        result.ErrorMessage.Should().Contain("indexed");
    }

    [Fact]
    public async Task HandleAsync_WithValidCitations_ParsesAndValidatesSources()
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

        var chunk1 = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            WorkspacePath = workspace.LocalPath,
            RelativePath = "src/Auth/AuthService.cs",
            Language = "csharp",
            SymbolName = "AuthService.AuthenticateAsync",
            TypeName = "AuthService",
            StartLine = 34,
            EndLine = 58,
            Content = "public async Task<AuthResult> AuthenticateAsync() { return null; }",
            ContentHash = "hash1",
        };

        var chunk2 = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            WorkspacePath = workspace.LocalPath,
            RelativePath = "src/Orders/OrderService.cs",
            Language = "csharp",
            SymbolName = "OrderService.CreateOrder",
            TypeName = "OrderService",
            StartLine = 12,
            EndLine = 40,
            Content = "public void CreateOrder() { }",
            ContentHash = "hash2",
        };

        _dbContext.CodeChunks.AddRange(chunk1, chunk2);
        await _dbContext.SaveChangesAsync();

        _aiProvider.ResponseToReturn = "Auth is handled in AuthService.\n\nSOURCES: [Source 1]";

        var result = await _handler.HandleAsync(new AskBrainCommand(workspace.Id, "How does auth work?"));

        result.Success.Should().BeTrue();
        result.Content.Should().Be("Auth is handled in AuthService.");
        result.Citations.Should().HaveCount(1);
        result.Citations[0].File.Should().Be("AuthService.cs");
        result.Citations[0].Path.Should().Be("src/Auth/AuthService.cs");
        result.Citations[0].Lines.Should().Be("L34–L58");
        result.Citations[0].Symbol.Should().Be("AuthService.AuthenticateAsync");
        result.Confidence.Should().BeGreaterThan(0);
        result.ContextFiles.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithInvalidSourceId_RejectsHallucinatedCitations()
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

        var chunk = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            WorkspacePath = workspace.LocalPath,
            RelativePath = "src/Auth/AuthService.cs",
            Language = "csharp",
            SymbolName = "AuthService.AuthenticateAsync",
            TypeName = "AuthService",
            StartLine = 34,
            EndLine = 58,
            Content = "public async Task<AuthResult> AuthenticateAsync() { return null; }",
            ContentHash = "hash1",
        };

        _dbContext.CodeChunks.Add(chunk);
        await _dbContext.SaveChangesAsync();

        // Model returns [Source 999] which doesn't exist
        _aiProvider.ResponseToReturn = "Here is an answer.\n\nSOURCES: [Source 999]";

        var result = await _handler.HandleAsync(new AskBrainCommand(workspace.Id, "How does auth work?"));

        result.Success.Should().BeTrue();
        result.Citations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_DoesNotLeakLocalPathInCitationsOrContext()
    {
        var workspace = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "testowner",
            Repository = "testrepo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = "C:/secret/server/path/workspace_123",
        };
        _dbContext.RepositoryWorkspaces.Add(workspace);

        var chunk = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            WorkspacePath = workspace.LocalPath,
            FilePath = "C:/secret/server/path/workspace_123/src/Test.cs",
            RelativePath = "src/Test.cs",
            Language = "csharp",
            SymbolName = "TestClass",
            StartLine = 1,
            EndLine = 10,
            Content = "class TestClass {}",
            ContentHash = "hash",
        };

        _dbContext.CodeChunks.Add(chunk);
        await _dbContext.SaveChangesAsync();

        _aiProvider.ResponseToReturn = "This is a test.\n\nSOURCES: [Source 1]";

        var result = await _handler.HandleAsync(new AskBrainCommand(workspace.Id, "Test query"));

        result.Citations[0].Path.Should().Be("src/Test.cs");
        result.Citations[0].Path.Should().NotContain("secret");
        result.ContextFiles[0].Path.Should().Be("src");
    }

    [Fact]
    public async Task HandleAsync_WhenAiProviderFails_ReturnsFailure()
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

        var chunk = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            WorkspacePath = workspace.LocalPath,
            RelativePath = "src/Test.cs",
            Language = "csharp",
            SymbolName = "TestClass",
            StartLine = 1,
            EndLine = 10,
            Content = "class TestClass {}",
            ContentHash = "hash",
        };
        _dbContext.CodeChunks.Add(chunk);
        await _dbContext.SaveChangesAsync();

        _aiProvider.IsSuccessToReturn = false;
        _aiProvider.ErrorMessageToReturn = "Rate limit exceeded";

        var result = await _handler.HandleAsync(new AskBrainCommand(workspace.Id, "Test query"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Rate limit exceeded");
    }

    [Fact]
    public async Task HandleAsync_AiRequest_DoesNotSetDeveloperAgentMaxTokensOverride()
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

        var chunk = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            WorkspacePath = workspace.LocalPath,
            RelativePath = "src/Test.cs",
            Language = "csharp",
            SymbolName = "TestClass",
            StartLine = 1,
            EndLine = 10,
            Content = "class TestClass {}",
            ContentHash = "hash",
        };
        _dbContext.CodeChunks.Add(chunk);
        await _dbContext.SaveChangesAsync();

        _aiProvider.ResponseToReturn = "Response text\n\nSOURCES: [Source 1]";

        var result = await _handler.HandleAsync(new AskBrainCommand(workspace.Id, "Test query"));

        result.Success.Should().BeTrue();
        _aiProvider.ReceivedRequests.Should().HaveCount(1);
        _aiProvider.ReceivedRequests[0].MaxTokens.Should().BeNull("Project Brain requests must not have the Developer Agent 16384 token limit applied");
    }
}
