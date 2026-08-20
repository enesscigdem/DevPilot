using DevPilot.Application.AiProviders;
using DevPilot.Application.ProjectBrain.Commands.AskBrain;
using DevPilot.Application.ProjectBrain.Commands.CreateBrainConversation;
using DevPilot.Application.ProjectBrain.Commands.DeleteBrainConversation;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.ProjectBrain.Queries.GetBrainConversationById;
using DevPilot.Application.ProjectBrain.Queries.GetBrainConversations;
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

public sealed class ProjectBrainConversationHistoryTests
{
    private readonly DevPilotDbContext _dbContext;
    private readonly IRepositoryWorkspaceQuery _workspaceQuery;
    private readonly ICodeChunkRepository _chunkRepository;
    private readonly IIndexJobRepository _jobRepository;
    private readonly ISemanticSearchService _searchService;
    private readonly ISemanticSearchQueryHandler _searchHandler;
    private readonly FakeAiProvider _aiProvider;
    private readonly IProjectBrainConversationRepository _conversationRepository;
    private readonly AskBrainCommandHandler _askHandler;
    private readonly GetBrainConversationsQueryHandler _getConversationsHandler;
    private readonly GetBrainConversationByIdQueryHandler _getConversationByIdHandler;
    private readonly CreateBrainConversationCommandHandler _createConversationHandler;
    private readonly DeleteBrainConversationCommandHandler _deleteConversationHandler;

    public ProjectBrainConversationHistoryTests()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase("BrainConversationTestsDb_" + Guid.NewGuid().ToString("N"))
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
        _conversationRepository = new EfProjectBrainConversationRepository(_dbContext);

        _askHandler = new AskBrainCommandHandler(
            _workspaceQuery,
            _chunkRepository,
            _jobRepository,
            _searchHandler,
            _aiProvider,
            _conversationRepository,
            NullLogger<AskBrainCommandHandler>.Instance);

        _getConversationsHandler = new GetBrainConversationsQueryHandler(_conversationRepository);
        _getConversationByIdHandler = new GetBrainConversationByIdQueryHandler(_conversationRepository);
        _createConversationHandler = new CreateBrainConversationCommandHandler(_conversationRepository);
        _deleteConversationHandler = new DeleteBrainConversationCommandHandler(_conversationRepository);
    }

    [Fact]
    public async Task Conversations_AreStrictlyScopedToWorkspace()
    {
        var ws1 = CreateWorkspace("repo-one");
        var ws2 = CreateWorkspace("repo-two");
        _dbContext.RepositoryWorkspaces.AddRange(ws1, ws2);
        await _dbContext.SaveChangesAsync();

        var conv1 = await _createConversationHandler.HandleAsync(new CreateBrainConversationCommand(ws1.Id, "Chat in WS 1"));
        var conv2 = await _createConversationHandler.HandleAsync(new CreateBrainConversationCommand(ws2.Id, "Chat in WS 2"));

        var ws1List = await _getConversationsHandler.HandleAsync(new GetBrainConversationsQuery(ws1.Id));
        var ws2List = await _getConversationsHandler.HandleAsync(new GetBrainConversationsQuery(ws2.Id));

        ws1List.Should().HaveCount(1);
        ws1List[0].Id.Should().Be(conv1.Id);
        ws1List[0].Title.Should().Be("Chat in WS 1");

        ws2List.Should().HaveCount(1);
        ws2List[0].Id.Should().Be(conv2.Id);
        ws2List[0].Title.Should().Be("Chat in WS 2");

        var crossLookup = await _getConversationByIdHandler.HandleAsync(new GetBrainConversationByIdQuery(ws1.Id, conv2.Id));
        crossLookup.Should().BeNull("Conversations belonging to another workspace must not be accessible");
    }

    [Fact]
    public async Task AskBrain_CreatesConversationAndPersistsMessagesWithGrounding()
    {
        var workspace = CreateWorkspace("test-repo");
        _dbContext.RepositoryWorkspaces.Add(workspace);

        var chunk = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            WorkspacePath = workspace.LocalPath,
            RelativePath = "src/Api/Controllers/ProductsController.cs",
            Language = "csharp",
            SymbolName = "ProductsController.GetProducts",
            TypeName = "ProductsController",
            StartLine = 20,
            EndLine = 45,
            Content = "public IActionResult GetProducts() => Ok();",
            ContentHash = "hash1",
        };
        _dbContext.CodeChunks.Add(chunk);
        await _dbContext.SaveChangesAsync();

        _aiProvider.ResponseToReturn = "Products are retrieved in ProductsController.\n\nSOURCES: [Source 1]";

        var chatResult = await _askHandler.HandleAsync(new AskBrainCommand(workspace.Id, "How are products retrieved?"));

        chatResult.Success.Should().BeTrue();
        chatResult.ConversationId.Should().NotBeNull();

        var convDetail = await _getConversationByIdHandler.HandleAsync(new GetBrainConversationByIdQuery(workspace.Id, chatResult.ConversationId!.Value));
        convDetail.Should().NotBeNull();
        convDetail!.Messages.Should().HaveCount(2);

        var userMsg = convDetail.Messages[0];
        userMsg.Role.Should().Be("user");
        userMsg.Content.Should().Be("How are products retrieved?");

        var assistantMsg = convDetail.Messages[1];
        assistantMsg.Role.Should().Be("assistant");
        assistantMsg.Content.Should().Be("Products are retrieved in ProductsController.");
        assistantMsg.Confidence.Should().BeGreaterThan(0);
        assistantMsg.Citations.Should().HaveCount(1);
        assistantMsg.Citations![0].File.Should().Be("ProductsController.cs");
        assistantMsg.ContextFiles.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AskBrain_ContinuingExistingConversation_AppendsMessages()
    {
        var workspace = CreateWorkspace("test-repo");
        _dbContext.RepositoryWorkspaces.Add(workspace);

        var chunk = new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            WorkspacePath = workspace.LocalPath,
            RelativePath = "src/Api/Controllers/OrdersController.cs",
            Language = "csharp",
            SymbolName = "OrdersController.Create",
            TypeName = "OrdersController",
            StartLine = 15,
            EndLine = 35,
            Content = "public IActionResult Create() => Ok();",
            ContentHash = "hash2",
        };
        _dbContext.CodeChunks.Add(chunk);
        await _dbContext.SaveChangesAsync();

        _aiProvider.ResponseToReturn = "Orders are handled here.\n\nSOURCES: [Source 1]";

        // First message creates conversation
        var firstResult = await _askHandler.HandleAsync(new AskBrainCommand(workspace.Id, "First question"));
        firstResult.ConversationId.Should().NotBeNull();
        var convId = firstResult.ConversationId!.Value;

        // Second message continues conversation
        _aiProvider.ResponseToReturn = "Second answer.\n\nSOURCES: [Source 1]";
        var secondResult = await _askHandler.HandleAsync(new AskBrainCommand(workspace.Id, "Second question", convId));
        secondResult.ConversationId.Should().Be(convId);

        var convDetail = await _getConversationByIdHandler.HandleAsync(new GetBrainConversationByIdQuery(workspace.Id, convId));
        convDetail.Should().NotBeNull();
        convDetail!.Messages.Should().HaveCount(4);
        convDetail.Messages[0].Content.Should().Be("First question");
        convDetail.Messages[1].Content.Should().Be("Orders are handled here.");
        convDetail.Messages[2].Content.Should().Be("Second question");
        convDetail.Messages[3].Content.Should().Be("Second answer.");
    }

    [Fact]
    public async Task DeleteConversation_RemovesConversationAndCascadesMessages()
    {
        var workspace = CreateWorkspace("test-repo");
        _dbContext.RepositoryWorkspaces.Add(workspace);
        await _dbContext.SaveChangesAsync();

        var conv = await _createConversationHandler.HandleAsync(new CreateBrainConversationCommand(workspace.Id, "To delete"));

        var deleted = await _deleteConversationHandler.HandleAsync(new DeleteBrainConversationCommand(workspace.Id, conv.Id));
        deleted.Should().BeTrue();

        var list = await _getConversationsHandler.HandleAsync(new GetBrainConversationsQuery(workspace.Id));
        list.Should().BeEmpty();
    }

    private static RepositoryWorkspace CreateWorkspace(string repoName)
    {
        return new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "testowner",
            Repository = repoName,
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = $"C:/fake/path/{repoName}",
        };
    }
}
