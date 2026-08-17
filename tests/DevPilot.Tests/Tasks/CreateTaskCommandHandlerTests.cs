using DevPilot.Application.Tasks.Commands.CreateTask;
using DevPilot.Application.Tasks.Dtos;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Tasks;

public class CreateTaskCommandHandlerTests
{
    private readonly InMemoryTaskRepository _taskRepository = new();
    private readonly InMemoryWorkspaceQuery _workspaceQuery = new();
    private readonly CreateTaskCommandHandler _handler;
    private readonly Guid _workspaceId = Guid.NewGuid();

    public CreateTaskCommandHandlerTests()
    {
        _workspaceQuery.Workspaces[_workspaceId] = new RepositoryWorkspace
        {
            Id = _workspaceId,
            Owner = "testowner",
            Repository = "testrepo",
            Branch = "main",
            LocalPath = "/tmp/repo",
            CommitSha = "abc1234",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _handler = new CreateTaskCommandHandler(
            _taskRepository,
            _workspaceQuery,
            NullLogger<CreateTaskCommandHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_ValidTitleAndDescription_CreatesTaskSuccessfully()
    {
        var dto = new CreateTaskDto
        {
            RepositoryWorkspaceId = _workspaceId,
            Title = "Implement rate limiting",
            Description = "Add rate limiting to public API endpoints.",
            Priority = DevelopmentTaskPriority.Medium,
        };

        var result = await _handler.HandleAsync(new CreateTaskCommand(dto));

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.Task.Should().NotBeNull();
        result.Task!.Title.Should().Be("Implement rate limiting");
        result.Task.Description.Should().Be("Add rate limiting to public API endpoints.");
        _taskRepository.Tasks.Should().HaveCount(1);
    }

    [Fact]
    public async Task HandleAsync_EmptyDescription_SucceedsWithEmptyDescription()
    {
        var dto = new CreateTaskDto
        {
            RepositoryWorkspaceId = _workspaceId,
            Title = "Implement rate limiting",
            Description = "",
            Priority = DevelopmentTaskPriority.Medium,
        };

        var result = await _handler.HandleAsync(new CreateTaskCommand(dto));

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.Task!.Title.Should().Be("Implement rate limiting");
        result.Task.Description.Should().Be("");
    }

    [Fact]
    public async Task HandleAsync_TitleExceeds200Chars_ReturnsValidationError()
    {
        var longTitle = new string('A', 201);
        var dto = new CreateTaskDto
        {
            RepositoryWorkspaceId = _workspaceId,
            Title = longTitle,
            Description = "Valid description.",
            Priority = DevelopmentTaskPriority.Medium,
        };

        var result = await _handler.HandleAsync(new CreateTaskCommand(dto));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Title must be at most 200 characters.");
        _taskRepository.Tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_DescriptionExceeds10000Chars_ReturnsValidationError()
    {
        var longDescription = new string('B', 10001);
        var dto = new CreateTaskDto
        {
            RepositoryWorkspaceId = _workspaceId,
            Title = "Valid title",
            Description = longDescription,
            Priority = DevelopmentTaskPriority.Medium,
        };

        var result = await _handler.HandleAsync(new CreateTaskCommand(dto));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Description must be at most 10,000 characters.");
        _taskRepository.Tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_DescriptionExact10000Chars_Succeeds()
    {
        var boundaryDescription = new string('C', 10000);
        var dto = new CreateTaskDto
        {
            RepositoryWorkspaceId = _workspaceId,
            Title = "Valid title",
            Description = boundaryDescription,
            Priority = DevelopmentTaskPriority.Medium,
        };

        var result = await _handler.HandleAsync(new CreateTaskCommand(dto));

        result.Success.Should().BeTrue();
        result.Task!.Description.Length.Should().Be(10000);
    }

    private sealed class InMemoryTaskRepository : ITaskRepository
    {
        public List<DevelopmentTask> Tasks { get; } = new();

        public Task AddAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            Tasks.Add(task);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            Tasks.RemoveAll(t => t.Id == task.Id);
            return Task.CompletedTask;
        }

        public Task<DevelopmentTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Tasks.FirstOrDefault(t => t.Id == id));
        }

        public Task<IReadOnlyList<DevelopmentTask>> GetAllAsync(DevelopmentTaskQueryFilter filter, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<DevelopmentTask>>(Tasks);
        }
    }

    private sealed class InMemoryWorkspaceQuery : IRepositoryWorkspaceQuery
    {
        public Dictionary<Guid, RepositoryWorkspace> Workspaces { get; } = new();

        public Task<RepositoryWorkspace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Workspaces.TryGetValue(id, out var ws) ? ws : null);
        }
    }
}
