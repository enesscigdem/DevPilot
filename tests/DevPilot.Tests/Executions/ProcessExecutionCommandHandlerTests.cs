using DevPilot.Application.Executions.Commands.ProcessExecution;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public class ProcessExecutionCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_ExecutionNotPending_ReturnsSkipped_AndDoesNotInvokeProcessor()
    {
        var executionId = Guid.NewGuid();
        var execution = new TaskExecution
        {
            Id = executionId,
            Status = TaskExecutionStatus.Running,
            DevelopmentTask = new DevelopmentTask
            {
                RepositoryWorkspace = new RepositoryWorkspace { LocalPath = "/some/path" }
            }
        };

        var repo = new TestExecutionRepository { ExecutionToReturn = execution, ClaimResult = false };
        var impactRepo = new TestImpactAnalysisRepository();
        var processor = new TestExecutionProcessor();

        var handler = new ProcessExecutionCommandHandler(repo, impactRepo, processor, NullLogger<ProcessExecutionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new ProcessExecutionCommand(executionId));

        result.Success.Should().BeTrue();
        result.Skipped.Should().BeTrue();
        processor.ProcessCallCount.Should().Be(0);
        repo.CompletedExecutionId.Should().BeNull();
        repo.FailedExecutionId.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_FullSuccess_CallsProcessor_AndPersistsCompleted()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        var tempDir = Path.Combine(Path.GetTempPath(), "DevPilotTestDir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var task = new DevelopmentTask
            {
                Id = taskId,
                Title = "Task Title",
                Description = "Task Desc",
                AcceptanceCriteria = "AC",
                RepositoryWorkspace = new RepositoryWorkspace
                {
                    Id = workspaceId,
                    LocalPath = tempDir
                }
            };

            var execution = new TaskExecution
            {
                Id = executionId,
                DevelopmentTaskId = taskId,
                DevelopmentTask = task,
                Status = TaskExecutionStatus.Pending
            };

            var repo = new TestExecutionRepository { ExecutionToReturn = execution, ClaimResult = true };
            var impactRepo = new TestImpactAnalysisRepository
            {
                AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed, Summary = "Summary" }
            };
            var processor = new TestExecutionProcessor();

            var handler = new ProcessExecutionCommandHandler(repo, impactRepo, processor, NullLogger<ProcessExecutionCommandHandler>.Instance);

            var result = await handler.HandleAsync(new ProcessExecutionCommand(executionId));

            result.Success.Should().BeTrue();
            result.Skipped.Should().BeFalse();
            processor.ProcessCallCount.Should().Be(1);
            repo.CompletedExecutionId.Should().Be(executionId);
            repo.FailedExecutionId.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HandleAsync_ProcessorThrowsException_PersistsSanitizedErrorMessage_AndFailsExecution()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        var tempDir = Path.Combine(Path.GetTempPath(), "DevPilotTestDir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var task = new DevelopmentTask
            {
                Id = taskId,
                Title = "Task Title",
                Description = "Task Desc",
                AcceptanceCriteria = "AC",
                RepositoryWorkspace = new RepositoryWorkspace
                {
                    Id = workspaceId,
                    LocalPath = tempDir
                }
            };

            var execution = new TaskExecution
            {
                Id = executionId,
                DevelopmentTaskId = taskId,
                DevelopmentTask = task,
                Status = TaskExecutionStatus.Pending
            };

            var repo = new TestExecutionRepository { ExecutionToReturn = execution, ClaimResult = true };
            var impactRepo = new TestImpactAnalysisRepository
            {
                AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed, Summary = "Summary" }
            };

            const string rawErrorMessage = "Build validation failed: dotnet build failed with exit code 1.\r\nAt line 123 in /some/path/file.cs\r\n   at Method() in StackTrace.cs:line 45";
            var processor = new TestExecutionProcessor { ExceptionToThrow = new InvalidOperationException(rawErrorMessage) };

            var handler = new ProcessExecutionCommandHandler(repo, impactRepo, processor, NullLogger<ProcessExecutionCommandHandler>.Instance);

            var result = await handler.HandleAsync(new ProcessExecutionCommand(executionId));

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Be("Build validation failed: dotnet build failed with exit code 1.");

            repo.FailedExecutionId.Should().Be(executionId);
            repo.FailedErrorMessage.Should().Be("Build validation failed: dotnet build failed with exit code 1.");
            repo.CompletedExecutionId.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SanitizeErrorMessage_StripsStackTraces_AndLimitsLength()
    {
        const string multilineError = "Developer Agent failed: Invalid schema\n  at DevAgent.Run()\n  at Process()";
        var sanitized = ProcessExecutionCommandHandler.SanitizeErrorMessage(multilineError);

        sanitized.Should().Be("Developer Agent failed: Invalid schema");
    }

    // ── Helper Test Fakes ──────────────────────────────────────────────────────────
    private class TestExecutionRepository : IExecutionRepository
    {
        public TaskExecution? ExecutionToReturn { get; set; }
        public bool ClaimResult { get; set; } = true;
        public Guid? CompletedExecutionId { get; private set; }
        public Guid? FailedExecutionId { get; private set; }
        public string? FailedErrorMessage { get; private set; }

        public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(ExecutionToReturn);
        public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskExecution>>(Array.Empty<TaskExecution>());
        public Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> StartExecutionAtomicAsync(TaskExecution execution, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(ClaimResult);

        public Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default)
        {
            CompletedExecutionId = executionId;
            return Task.CompletedTask;
        }

        public Task FailAsync(Guid executionId, string errorMessage, CancellationToken cancellationToken = default)
        {
            FailedExecutionId = executionId;
            FailedErrorMessage = errorMessage;
            return Task.CompletedTask;
        }

        public Task UpdateWorkspaceDetailsAsync(Guid executionId, string workspacePath, string branchName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class TestImpactAnalysisRepository : IImpactAnalysisRepository
    {
        public TaskImpactAnalysis? AnalysisToReturn { get; set; }

        public Task<TaskImpactAnalysis?> GetLatestByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(AnalysisToReturn);

        public Task AddAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class TestExecutionProcessor : IExecutionProcessor
    {
        public Exception? ExceptionToThrow { get; set; }
        public int ProcessCallCount { get; private set; }

        public Task ProcessAsync(ExecutionProcessingContext context, CancellationToken cancellationToken = default)
        {
            ProcessCallCount++;
            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }
            return Task.CompletedTask;
        }
    }
}
