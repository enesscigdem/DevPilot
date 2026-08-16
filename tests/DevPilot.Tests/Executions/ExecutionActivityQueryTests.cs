using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Queries.GetExecutionActivity;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Executions;

public class ExecutionActivityQueryTests
{
    [Fact]
    public async Task HandleAsync_ExecutionNotFound_ReturnsFoundFalse()
    {
        var executionId = Guid.NewGuid();
        var repo = new TestExecutionRepository { ExecutionToReturn = null };
        var activityRepo = new TestActivityRepository();
        var handler = new GetExecutionActivityQueryHandler(repo, activityRepo);

        var result = await handler.HandleAsync(new GetExecutionActivityQuery(executionId));

        result.Found.Should().BeFalse();
        result.ErrorMessage.Should().Contain(executionId.ToString());
        result.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_HistoricalExecutionWithNoActivities_ReturnsFoundTrue_AndEmptyActivitiesList()
    {
        var executionId = Guid.NewGuid();
        var repo = new TestExecutionRepository
        {
            ExecutionToReturn = new TaskExecution
            {
                Id = executionId,
                Status = TaskExecutionStatus.Completed
            }
        };
        var activityRepo = new TestActivityRepository();
        var handler = new GetExecutionActivityQueryHandler(repo, activityRepo);

        var result = await handler.HandleAsync(new GetExecutionActivityQuery(executionId));

        result.Found.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ReturnsActivitiesInDeterministicChronologicalOrder()
    {
        var executionId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var repo = new TestExecutionRepository
        {
            ExecutionToReturn = new TaskExecution
            {
                Id = executionId,
                Status = TaskExecutionStatus.Completed
            }
        };

        var activityRepo = new TestActivityRepository
        {
            ActivitiesToReturn = new List<ExecutionActivity>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ExecutionId = executionId,
                    Stage = ExecutionStage.Execution,
                    Status = ExecutionActivityStatus.Started,
                    CreatedAt = now.AddSeconds(-10),
                    Message = "Execution started."
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    ExecutionId = executionId,
                    Stage = ExecutionStage.Workspace,
                    Status = ExecutionActivityStatus.Completed,
                    CreatedAt = now.AddSeconds(-5),
                    Message = "Workspace prepared.",
                    MetadataJson = "{\"branchName\":\"devpilot/task-1\"}"
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    ExecutionId = executionId,
                    Stage = ExecutionStage.DeveloperAgent,
                    Status = ExecutionActivityStatus.Completed,
                    CreatedAt = now.AddSeconds(-2),
                    Message = "Developer Agent completed.",
                    MetadataJson = "{\"modifiedFileCount\":1}"
                }
            }
        };

        var handler = new GetExecutionActivityQueryHandler(repo, activityRepo);

        var result = await handler.HandleAsync(new GetExecutionActivityQuery(executionId));

        result.Found.Should().BeTrue();
        result.Activities.Should().HaveCount(3);
        result.Activities[0].Stage.Should().Be("Execution");
        result.Activities[0].Status.Should().Be("Started");
        result.Activities[1].Stage.Should().Be("Workspace");
        result.Activities[1].Metadata?.BranchName.Should().Be("devpilot/task-1");
        result.Activities[2].Stage.Should().Be("DeveloperAgent");
        result.Activities[2].Metadata?.ModifiedFileCount.Should().Be(1);
    }

    private class TestExecutionRepository : IExecutionRepository
    {
        public TaskExecution? ExecutionToReturn { get; set; }

        public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(ExecutionToReturn);
        public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskExecution>>(Array.Empty<TaskExecution>());
        public Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> StartExecutionAtomicAsync(TaskExecution execution, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailAsync(Guid executionId, string errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateWorkspaceDetailsAsync(Guid executionId, string workspacePath, string branchName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TrySetReviewDecisionAsync(Guid executionId, DevPilot.Domain.Enums.ExecutionReviewStatus expectedStatus, DevPilot.Domain.Enums.ExecutionReviewStatus newStatus, DateTime decidedAt, string? rejectionReason, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private class TestActivityRepository : IExecutionActivityRepository
    {
        public List<ExecutionActivity> ActivitiesToReturn { get; set; } = new();

        public Task<IReadOnlyList<ExecutionActivity>> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionActivity>>(ActivitiesToReturn);
    }
}
