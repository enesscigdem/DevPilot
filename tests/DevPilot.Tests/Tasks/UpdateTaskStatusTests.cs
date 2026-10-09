using DevPilot.Application.Tasks.Commands.UpdateTaskStatus;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Tasks;

public sealed class UpdateTaskStatusTests
{
    [Theory]
    [InlineData(DevelopmentTaskStatus.Draft, DevelopmentTaskStatus.Approved)]
    [InlineData(DevelopmentTaskStatus.AwaitingApproval, DevelopmentTaskStatus.Approved)]
    [InlineData(DevelopmentTaskStatus.Approved, DevelopmentTaskStatus.Executing)]
    [InlineData(DevelopmentTaskStatus.Draft, DevelopmentTaskStatus.Completed)]
    [InlineData(DevelopmentTaskStatus.Draft, DevelopmentTaskStatus.Failed)]
    [InlineData(DevelopmentTaskStatus.Draft, DevelopmentTaskStatus.AwaitingApproval)]
    [InlineData(DevelopmentTaskStatus.Executing, DevelopmentTaskStatus.Draft)]
    [InlineData(DevelopmentTaskStatus.Analyzing, DevelopmentTaskStatus.Rejected)]
    [InlineData(DevelopmentTaskStatus.Completed, DevelopmentTaskStatus.Draft)]
    [InlineData(DevelopmentTaskStatus.Draft, DevelopmentTaskStatus.Draft)]
    public void Steps_that_skip_approval_or_touch_running_work_are_refused(DevelopmentTaskStatus from, DevelopmentTaskStatus to) =>
        ManualStatusChangePolicy.IsAllowed(from, to).Should().BeFalse();

    [Theory]
    [InlineData(DevelopmentTaskStatus.Draft, DevelopmentTaskStatus.ReadyForAnalysis)]
    [InlineData(DevelopmentTaskStatus.ReadyForAnalysis, DevelopmentTaskStatus.Draft)]
    [InlineData(DevelopmentTaskStatus.AwaitingApproval, DevelopmentTaskStatus.Rejected)]
    [InlineData(DevelopmentTaskStatus.Approved, DevelopmentTaskStatus.Rejected)]
    [InlineData(DevelopmentTaskStatus.Failed, DevelopmentTaskStatus.Draft)]
    public void Safe_manual_moves_stay_possible(DevelopmentTaskStatus from, DevelopmentTaskStatus to) =>
        ManualStatusChangePolicy.IsAllowed(from, to).Should().BeTrue();

    [Fact]
    public async Task The_handler_refuses_a_forbidden_move_without_saving()
    {
        var task = new DevelopmentTask { Id = Guid.NewGuid(), Status = DevelopmentTaskStatus.AwaitingApproval };
        var repository = new FakeRepository(task);
        var handler = new UpdateTaskStatusCommandHandler(repository, NullLogger<UpdateTaskStatusCommandHandler>.Instance);

        var result = await handler.HandleAsync(new UpdateTaskStatusCommand(task.Id, DevelopmentTaskStatus.Approved));

        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        task.Status.Should().Be(DevelopmentTaskStatus.AwaitingApproval);
        repository.Updates.Should().Be(0);
    }

    [Fact]
    public async Task The_handler_saves_an_allowed_move()
    {
        var task = new DevelopmentTask { Id = Guid.NewGuid(), Status = DevelopmentTaskStatus.Draft };
        var repository = new FakeRepository(task);
        var handler = new UpdateTaskStatusCommandHandler(repository, NullLogger<UpdateTaskStatusCommandHandler>.Instance);

        var result = await handler.HandleAsync(new UpdateTaskStatusCommand(task.Id, DevelopmentTaskStatus.ReadyForAnalysis));

        result.Success.Should().BeTrue();
        task.Status.Should().Be(DevelopmentTaskStatus.ReadyForAnalysis);
        repository.Updates.Should().Be(1);
    }

    private sealed class FakeRepository : ITaskRepository
    {
        private readonly DevelopmentTask _task;

        public FakeRepository(DevelopmentTask task) => _task = task;

        public int Updates { get; private set; }

        public Task AddAsync(DevelopmentTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            Updates++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(DevelopmentTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DevelopmentTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<DevelopmentTask?>(_task);

        public Task<IReadOnlyList<DevelopmentTask>> GetAllAsync(DevelopmentTaskQueryFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DevelopmentTask>>(new[] { _task });
    }
}
