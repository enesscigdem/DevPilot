using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Domain.ProjectBrain.Entities;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.ProjectBrain.SemanticSearch;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.ProjectBrain;

public sealed class EfSemanticSearchServiceTests
{
    private readonly DevPilotDbContext _dbContext;
    private readonly EfSemanticSearchService _service;

    public EfSemanticSearchServiceTests()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase("SemanticSearchTestDb_" + Guid.NewGuid().ToString("N"))
            .Options;

        _dbContext = new DevPilotDbContext(options);
        _service = new EfSemanticSearchService(_dbContext, NullLogger<EfSemanticSearchService>.Instance);
    }

    [Fact]
    public async Task SearchAsync_LexicalSearch_MatchesSymbolsAndRanksCorrectly()
    {
        var workspaceId = Guid.NewGuid();

        var chunkAuth = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspaceId,
            RelativePath = "src/Auth/AuthService.cs",
            SymbolName = "AuthService.AuthenticateAsync",
            TypeName = "AuthService",
            Content = "public async Task<AuthResult> AuthenticateAsync(string email, string password) { return null; }",
            ContentHash = "h1",
        };

        var chunkOrder = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspaceId,
            RelativePath = "src/Orders/OrderService.cs",
            SymbolName = "OrderService.CalculatePrice",
            TypeName = "OrderService",
            Content = "public decimal CalculatePrice(Order order) { return 0m; }",
            ContentHash = "h2",
        };

        _dbContext.CodeChunks.AddRange(chunkAuth, chunkOrder);
        await _dbContext.SaveChangesAsync();

        var query = new SemanticSearchQuery
        {
            RepositoryWorkspaceId = workspaceId,
            QueryText = "How does authentication work?",
            MaxResults = 5,
        };

        var result = await _service.SearchAsync(query, null);

        result.Success.Should().BeTrue();
        result.RetrievalMode.Should().Be("lexical");
        result.Hits.Should().NotBeEmpty();
        result.Hits[0].Chunk.RelativePath.Should().Be("src/Auth/AuthService.cs");
        result.Hits[0].Score.Should().BeGreaterThan(0.5);
    }

    [Fact]
    public async Task SearchAsync_RespectsWorkspaceScoping_DoesNotLeakAcrossWorkspaces()
    {
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();

        var chunkA = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspaceA,
            RelativePath = "src/SecretA.cs",
            SymbolName = "SecretClassA",
            Content = "class SecretClassA {}",
            ContentHash = "ha",
        };

        var chunkB = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspaceB,
            RelativePath = "src/SecretB.cs",
            SymbolName = "SecretClassB",
            Content = "class SecretClassB {}",
            ContentHash = "hb",
        };

        _dbContext.CodeChunks.AddRange(chunkA, chunkB);
        await _dbContext.SaveChangesAsync();

        var query = new SemanticSearchQuery
        {
            RepositoryWorkspaceId = workspaceA,
            QueryText = "SecretClass",
            MaxResults = 10,
        };

        var result = await _service.SearchAsync(query, null);

        result.Success.Should().BeTrue();
        result.Hits.Should().HaveCount(1);
        result.Hits[0].Chunk.RepositoryWorkspaceId.Should().Be(workspaceA);
        result.Hits[0].Chunk.RelativePath.Should().Be("src/SecretA.cs");
    }
}
