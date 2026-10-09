using DevPilot.Application.Tasks.Commands.UpdateTask;
using DevPilot.Application.Tasks.Dtos;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Tasks;

/// <summary>An approval belongs to the text it was given for: changing that text voids it, and running work keeps its input.</summary>
public sealed class UpdateTaskApprovalTests
{
    private static readonly RepositoryWorkspace Workspace = new() { Id = Guid.NewGuid(), Owner = "o", Repository = "r" };

    private static DevelopmentTask Make(DevelopmentTaskStatus status) => new()
    {
        Id = Guid.NewGuid(),
        RepositoryWorkspaceId = Workspace.Id,
        RepositoryWorkspace = Workspace,
        Title = "Title",
        Description = "Description",
        AcceptanceCriteria = "Criteria",
        Priority = DevelopmentTaskPriority.Medium,
        Status = status,
    };

    private static UpdateTaskDto Dto(string description = "Description", DevelopmentTaskPriority priority = DevelopmentTaskPriority.Medium) =>
        new() { Title = "Title", Description = description, AcceptanceCriteria = "Criteria", Priority = priority };

    private static async Task<(UpdateTaskResult Result, Repo Repo)> Update(DevelopmentTask task, UpdateTaskDto dto)
    {
        var repo = new Repo(task);
        var handler = new UpdateTaskCommandHandler(repo, new Query(), NullLogger<UpdateTaskCommandHandler>.Instance);
        return (await handler.HandleAsync(new UpdateTaskCommand(task.Id, dto)), repo);
    }

    [Theory]
    [InlineData(DevelopmentTaskStatus.AwaitingApproval)]
    [InlineData(DevelopmentTaskStatus.Approved)]
    [InlineData(DevelopmentTaskStatus.Failed)]
    public async Task Changing_the_text_sends_a_task_back_for_a_new_analysis(DevelopmentTaskStatus status)
    {
        var task = Make(status);

        var (result, _) = await Update(task, Dto("A different request"));

        result.Success.Should().BeTrue();
        result.ApprovalReset.Should().BeTrue();
        task.Status.Should().Be(DevelopmentTaskStatus.ReadyForAnalysis);
        task.Description.Should().Be("A different request");
    }

    [Theory]
    [InlineData(DevelopmentTaskStatus.Analyzing)]
    [InlineData(DevelopmentTaskStatus.Executing)]
    [InlineData(DevelopmentTaskStatus.Completed)]
    public async Task The_text_of_work_that_is_running_or_delivered_cannot_change(DevelopmentTaskStatus status)
    {
        var task = Make(status);

        var (result, repo) = await Update(task, Dto("A different request"));

        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        task.Description.Should().Be("Description");
        repo.Updates.Should().Be(0);
    }

    [Fact]
    public async Task A_change_of_priority_alone_keeps_the_approval()
    {
        var task = Make(DevelopmentTaskStatus.Approved);

        var (result, _) = await Update(task, Dto(priority: DevelopmentTaskPriority.High));

        result.Success.Should().BeTrue();
        result.ApprovalReset.Should().BeFalse();
        task.Status.Should().Be(DevelopmentTaskStatus.Approved);
        task.Priority.Should().Be(DevelopmentTaskPriority.High);
    }

    [Fact]
    public async Task Text_of_a_draft_changes_freely()
    {
        var task = Make(DevelopmentTaskStatus.Draft);

        var (result, _) = await Update(task, Dto("A different request"));

        result.Success.Should().BeTrue();
        result.ApprovalReset.Should().BeFalse();
        task.Status.Should().Be(DevelopmentTaskStatus.Draft);
    }

    [Fact]
    public async Task A_missing_task_is_not_found_and_a_blank_title_is_a_validation_error()
    {
        var repo = new Repo(null);
        var handler = new UpdateTaskCommandHandler(repo, new Query(), NullLogger<UpdateTaskCommandHandler>.Instance);

        var missing = await handler.HandleAsync(new UpdateTaskCommand(Guid.NewGuid(), Dto()));
        var invalid = await handler.HandleAsync(new UpdateTaskCommand(Guid.NewGuid(), new UpdateTaskDto { Title = " ", Description = "d" }));

        missing.NotFound.Should().BeTrue();
        invalid.NotFound.Should().BeFalse();
        invalid.Success.Should().BeFalse();
    }

    private sealed class Query : IRepositoryWorkspaceQuery
    {
        public Task<RepositoryWorkspace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<RepositoryWorkspace?>(Workspace);
    }

    private sealed class Repo : ITaskRepository
    {
        private readonly DevelopmentTask? _task;

        public Repo(DevelopmentTask? task) => _task = task;

        public int Updates { get; private set; }

        public Task AddAsync(DevelopmentTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            Updates++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(DevelopmentTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DevelopmentTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_task);

        public Task<IReadOnlyList<DevelopmentTask>> GetAllAsync(DevelopmentTaskQueryFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DevelopmentTask>>(_task is null ? Array.Empty<DevelopmentTask>() : new[] { _task });
    }
}
