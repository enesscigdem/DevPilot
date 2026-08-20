using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class InMemoryExecutionRepositoryConcurrencyTests
{
    [Fact]
    public async Task StartExecutionAtomicAsync_WhenConcurrentCallsForSameTask_OnlyOneSucceeds()
    {
        var repo = new InMemoryExecutionRepository();
        var taskId = Guid.NewGuid();
        var task = new DevelopmentTask
        {
            Id = taskId,
            Title = "Concurrency Task",
            Status = DevelopmentTaskStatus.Executing,
        };

        const int concurrencyLevel = 10;
        var barrier = new Barrier(concurrencyLevel);

        var tasks = Enumerable.Range(0, concurrencyLevel).Select(_ => Task.Run(async () =>
        {
            var exec = new TaskExecution
            {
                Id = Guid.NewGuid(),
                DevelopmentTaskId = taskId,
                Status = TaskExecutionStatus.Pending,
            };

            barrier.SignalAndWait();
            return await repo.StartExecutionAtomicAsync(exec, task);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r).Should().Be(1, "Exactly one execution insert must succeed for the same task");
        results.Count(r => !r).Should().Be(concurrencyLevel - 1, "All other concurrent inserts must return false (conflict)");
    }

    [Fact]
    public async Task StartExecutionAtomicAsync_WhenConcurrentCallsForDifferentTasks_AllSucceed()
    {
        var repo = new InMemoryExecutionRepository();
        const int taskCount = 10;
        var barrier = new Barrier(taskCount);

        var tasks = Enumerable.Range(0, taskCount).Select(_ => Task.Run(async () =>
        {
            var taskId = Guid.NewGuid();
            var task = new DevelopmentTask
            {
                Id = taskId,
                Title = $"Task {taskId}",
                Status = DevelopmentTaskStatus.Executing,
            };

            var exec = new TaskExecution
            {
                Id = Guid.NewGuid(),
                DevelopmentTaskId = taskId,
                Status = TaskExecutionStatus.Pending,
            };

            barrier.SignalAndWait();
            return await repo.StartExecutionAtomicAsync(exec, task);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Should().BeTrue("Different tasks are allowed to start executions concurrently"));
    }
}
