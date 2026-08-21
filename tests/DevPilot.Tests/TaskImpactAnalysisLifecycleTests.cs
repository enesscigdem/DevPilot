using DevPilot.Application.AiProviders;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.TaskImpactAnalysis.Commands.AnalyzeTaskImpact;
using DevPilot.Application.TaskImpactAnalysis.Dtos;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.TaskImpactAnalysis.Queries.GetTaskImpactAnalysis;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ProjectBrain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TaskImpactAnalysisEntity = DevPilot.Domain.Entities.TaskImpactAnalysis;

namespace DevPilot.Tests;

public sealed class TaskImpactAnalysisLifecycleTests
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly RepositoryWorkspace _workspace;
    private readonly string _repoRoot;

    public TaskImpactAnalysisLifecycleTests()
    {
        var currentDir = AppDomain.CurrentDomain.BaseDirectory;
        var dir = new DirectoryInfo(currentDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DevPilot.sln")))
        {
            dir = dir.Parent;
        }
        _repoRoot = dir?.FullName ?? throw new InvalidOperationException("Could not locate DevPilot.sln repository root.");

        _workspace = new RepositoryWorkspace
        {
            Id = _workspaceId,
            Owner = "testowner",
            Repository = "testrepo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = _repoRoot,
            CommitSha = "abc1234",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    [Fact]
    public async Task HandleAsync_ConcurrentRequests_OnlyOneSucceeds_SecondReceivesConflict()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = new DevelopmentTask
        {
            Id = taskId,
            RepositoryWorkspaceId = _workspaceId,
            Title = "Implement concurrency guard",
            Description = "Requirement description",
            Status = DevelopmentTaskStatus.Draft,
            Priority = DevelopmentTaskPriority.Medium,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var taskRepo = new ConcurrentInMemoryTaskRepository();
        taskRepo.Add(task);

        var analysisRepo = new ConcurrentInMemoryAnalysisRepository();

        var enteredAiTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAiTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var delayedAi = new BlockingAiProvider(
            enteredAiTcs,
            releaseAiTcs.Task,
            new AiResponse
            {
                IsSuccess = true,
                Content = "{\"Summary\":\"Impact summary\",\"Confidence\":88,\"ImpactedFiles\":[{\"FilePath\":\"src/DevPilot.Api/Controllers/TasksController.cs\",\"ChangeType\":\"Modify\",\"Reason\":\"Updated endpoint\",\"Confidence\":90}],\"ProposedPlan\":[{\"Order\":1,\"Title\":\"Update code\",\"Description\":\"Edit controller\",\"RelatedFiles\":[\"src/DevPilot.Api/Controllers/TasksController.cs\"]}],\"SystemImpacts\":[{\"Area\":\"API Surface\",\"ImpactLevel\":\"Medium\",\"Description\":\"Controller update\"}],\"Risks\":[{\"Level\":\"Low\",\"Description\":\"Minor\",\"Mitigation\":\"Unit tests\"}]}",
                Model = "gemini-2.5-pro",
                Provider = "Gemini",
            });

        var handler1 = new AnalyzeTaskImpactCommandHandler(
            taskRepo,
            new FakeWorkspaceQuery { WorkspaceToReturn = _workspace },
            analysisRepo,
            new FakeRepositoryAnalyzer(),
            delayedAi,
            new FakeEmbeddingProvider(),
            new FakeSearchService(),
            NullLogger<AnalyzeTaskImpactCommandHandler>.Instance);

        var handler2 = CreateHandler(taskRepo, analysisRepo);

        // Act: Start request 1, wait until it enters AI phase (staged as InProgress in DB)
        var executionTask1 = Task.Run(() => handler1.HandleAsync(new AnalyzeTaskImpactCommand(taskId), CancellationToken.None));
        await enteredAiTcs.Task;

        // Start request 2 while request 1 is still actively running in InProgress state
        var result2 = await handler2.HandleAsync(new AnalyzeTaskImpactCommand(taskId), CancellationToken.None);

        // Now release request 1
        releaseAiTcs.SetResult(true);
        var result1 = await executionTask1;

        // Assert: Request 1 succeeds, Request 2 is rejected with 409 Conflict
        result1.Success.Should().BeTrue();
        result1.Conflict.Should().BeFalse();

        result2.Success.Should().BeFalse();
        result2.Conflict.Should().BeTrue();
        result2.ErrorMessage.Should().Contain("already in progress");
    }

    [Fact]
    public async Task HandleAsync_WhenTaskInExecutingOrCompletedStatus_RejectsWithConflict()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = new DevelopmentTask
        {
            Id = taskId,
            RepositoryWorkspaceId = _workspaceId,
            Title = "Task in executing status",
            Description = "Requirement description",
            Status = DevelopmentTaskStatus.Executing,
            Priority = DevelopmentTaskPriority.Medium,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var taskRepo = new ConcurrentInMemoryTaskRepository();
        taskRepo.Add(task);
        var analysisRepo = new ConcurrentInMemoryAnalysisRepository();
        var handler = CreateHandler(taskRepo, analysisRepo);

        // Act
        var result = await handler.HandleAsync(new AnalyzeTaskImpactCommand(taskId), CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("Executing");
    }

    [Fact]
    public async Task HandleAsync_WhenStaleInProgressAnalysisExists_ReconcilesToFailedBeforeStartingNewAnalysis()
    {
        // Arrange: Task stuck in Analyzing with an InProgress analysis from 10 minutes ago
        var taskId = Guid.NewGuid();
        var task = new DevelopmentTask
        {
            Id = taskId,
            RepositoryWorkspaceId = _workspaceId,
            Title = "Stuck task",
            Description = "Requirement description",
            Status = DevelopmentTaskStatus.Analyzing,
            Priority = DevelopmentTaskPriority.Medium,
            CreatedAt = DateTime.UtcNow.AddMinutes(-15),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
        };

        var staleAnalysis = new TaskImpactAnalysisEntity
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskId,
            Status = ImpactAnalysisStatus.InProgress,
            Summary = string.Empty,
            Confidence = 0,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = null,
        };

        var taskRepo = new ConcurrentInMemoryTaskRepository();
        taskRepo.Add(task);

        var analysisRepo = new ConcurrentInMemoryAnalysisRepository();
        analysisRepo.Add(staleAnalysis);

        var handler = CreateHandler(taskRepo, analysisRepo);

        // Act: Start analysis
        var result = await handler.HandleAsync(new AnalyzeTaskImpactCommand(taskId), CancellationToken.None);

        // Assert: Succeeded because stale was reconciled and cleared
        result.Success.Should().BeTrue();
        result.Analysis.Should().NotBeNull();
        result.Analysis!.Status.Should().Be(ImpactAnalysisStatus.Completed);

        // Verify stale analysis was transitioned to Failed
        staleAnalysis.Status.Should().Be(ImpactAnalysisStatus.Failed);
        staleAnalysis.ErrorMessage.Should().Be("Impact analysis did not complete before the execution timeout.");
    }

    [Fact]
    public async Task GetTaskImpactAnalysisQueryHandler_WhenStaleInProgressAnalysisExists_AutoReconcilesToFailed()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var staleAnalysis = new TaskImpactAnalysisEntity
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskId,
            Status = ImpactAnalysisStatus.InProgress,
            Summary = string.Empty,
            Confidence = 0,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = null,
        };

        var analysisRepo = new ConcurrentInMemoryAnalysisRepository();
        analysisRepo.Add(staleAnalysis);

        var queryHandler = new GetTaskImpactAnalysisQueryHandler(analysisRepo);

        // Act
        var result = await queryHandler.HandleAsync(new GetTaskImpactAnalysisQuery(taskId), CancellationToken.None);

        // Assert
        result.Found.Should().BeTrue();
        result.Analysis.Should().NotBeNull();
        result.Analysis!.Status.Should().Be(ImpactAnalysisStatus.Failed);
        result.Analysis!.ErrorMessage.Should().Be("Impact analysis did not complete before the execution timeout.");
    }

    [Fact]
    public async Task HandleAsync_WhenAiProviderFails_PersistsFailedAnalysisAndMarksTaskFailed()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = new DevelopmentTask
        {
            Id = taskId,
            RepositoryWorkspaceId = _workspaceId,
            Title = "Task with failing AI",
            Description = "Requirement description",
            Status = DevelopmentTaskStatus.Draft,
            Priority = DevelopmentTaskPriority.Medium,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var taskRepo = new ConcurrentInMemoryTaskRepository();
        taskRepo.Add(task);
        var analysisRepo = new ConcurrentInMemoryAnalysisRepository();

        var failingAi = new FakeAiProvider
        {
            ResponseToReturn = new AiResponse
            {
                IsSuccess = false,
                ErrorMessage = "AI quota exceeded",
            }
        };

        var handler = new AnalyzeTaskImpactCommandHandler(
            taskRepo,
            new FakeWorkspaceQuery { WorkspaceToReturn = _workspace },
            analysisRepo,
            new FakeRepositoryAnalyzer(),
            failingAi,
            new FakeEmbeddingProvider(),
            new FakeSearchService(),
            NullLogger<AnalyzeTaskImpactCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new AnalyzeTaskImpactCommand(taskId), CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.AnalysisId.Should().NotBeNull();
        result.ErrorMessage.Should().Be("AI quota exceeded");

        task.Status.Should().Be(DevelopmentTaskStatus.Failed);
        var persisted = await analysisRepo.GetLatestByTaskIdAsync(taskId);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(ImpactAnalysisStatus.Failed);
        persisted.ErrorMessage.Should().Be("AI quota exceeded");
    }

    [Fact]
    public async Task HandleAsync_InitialCallTruncated_ExactlyOneCompactRecoverySucceeds()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = new DevelopmentTask
        {
            Id = taskId,
            RepositoryWorkspaceId = _workspaceId,
            Title = "Siparişlere tahmini teslim tarihi ekleyelim",
            Description = "Order estimated delivery date feature",
            Status = DevelopmentTaskStatus.Draft,
            Priority = DevelopmentTaskPriority.Medium,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var taskRepo = new ConcurrentInMemoryTaskRepository();
        taskRepo.Add(task);
        var analysisRepo = new ConcurrentInMemoryAnalysisRepository();

        var sequentialAi = new SequentialAiProvider(
            // Call 1: Truncated response
            new AiResponse
            {
                IsSuccess = false,
                FailureKind = AiFailureKind.TokenLimitExceeded,
                FinishReason = "length",
                ErrorMessage = "AI response exhausted the configured output token limit before producing a complete result.",
                OutputTokens = 2048,
            },
            // Call 2: Recovery response
            new AiResponse
            {
                IsSuccess = true,
                Content = "{\"Summary\":\"Add estimated delivery date to orders\",\"Confidence\":92,\"ImpactedFiles\":[{\"FilePath\":\"src/DevPilot.Domain/Entities/DevelopmentTask.cs\",\"ChangeType\":\"Modify\",\"Reason\":\"Add field\"}],\"Dimensions\":[{\"Area\":\"DATA\",\"ImpactLevel\":\"Medium\",\"Summary\":\"Order schema update\"}],\"ProposedPlan\":[{\"Order\":1,\"Title\":\"Update entity\",\"Description\":\"Add EstimatedDeliveryDate\"}],\"Risks\":[{\"Level\":\"Low\",\"Description\":\"Minor migration\"}]}",
                Model = "kimi-k3",
                Provider = "Kimi",
                OutputTokens = 240,
                FinishReason = "stop"
            });

        var handler = new AnalyzeTaskImpactCommandHandler(
            taskRepo,
            new FakeWorkspaceQuery { WorkspaceToReturn = _workspace },
            analysisRepo,
            new FakeRepositoryAnalyzer(),
            sequentialAi,
            new FakeEmbeddingProvider(),
            new FakeSearchService(),
            NullLogger<AnalyzeTaskImpactCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new AnalyzeTaskImpactCommand(taskId), CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Analysis.Should().NotBeNull();
        result.Analysis!.Summary.Should().Be("Add estimated delivery date to orders");
        result.Analysis.StructuredResult.Should().NotBeNull();
        result.Analysis.StructuredResult!.ImpactedFiles.Should().HaveCount(1);
        result.Analysis.StructuredResult.ImpactedFiles[0].FilePath.Should().Be("src/DevPilot.Domain/Entities/DevelopmentTask.cs");

        // Exactly two provider calls were made
        sequentialAi.CallCount.Should().Be(2);
        sequentialAi.RecordedRequests[0].MaxTokens.Should().Be(2048);
        sequentialAi.RecordedRequests[1].MaxTokens.Should().Be(2048);
        sequentialAi.RecordedRequests[1].UserPrompt.Should().Contain("CRITICAL: The previous response was truncated");

        task.Status.Should().Be(DevelopmentTaskStatus.AwaitingApproval);
    }

    [Fact]
    public async Task HandleAsync_RecoveryCallAlsoTruncated_FailsClearlyWithoutLooping()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = new DevelopmentTask
        {
            Id = taskId,
            RepositoryWorkspaceId = _workspaceId,
            Title = "Heavy task that exceeds limits twice",
            Description = "Requirement description",
            Status = DevelopmentTaskStatus.Draft,
            Priority = DevelopmentTaskPriority.High,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var taskRepo = new ConcurrentInMemoryTaskRepository();
        taskRepo.Add(task);
        var analysisRepo = new ConcurrentInMemoryAnalysisRepository();

        var sequentialAi = new SequentialAiProvider(
            // Call 1: Truncated response
            new AiResponse
            {
                IsSuccess = false,
                FailureKind = AiFailureKind.TokenLimitExceeded,
                FinishReason = "length",
                ErrorMessage = "AI response exhausted the configured output token limit before producing a complete result.",
            },
            // Call 2: Recovery also truncated
            new AiResponse
            {
                IsSuccess = false,
                FailureKind = AiFailureKind.TokenLimitExceeded,
                FinishReason = "length",
                ErrorMessage = "AI response exhausted the configured output token limit before producing a complete result.",
            });

        var handler = new AnalyzeTaskImpactCommandHandler(
            taskRepo,
            new FakeWorkspaceQuery { WorkspaceToReturn = _workspace },
            analysisRepo,
            new FakeRepositoryAnalyzer(),
            sequentialAi,
            new FakeEmbeddingProvider(),
            new FakeSearchService(),
            NullLogger<AnalyzeTaskImpactCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new AnalyzeTaskImpactCommand(taskId), CancellationToken.None);

        // Assert: Stops after exactly 2 calls, fails cleanly
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exhausted token limit on initial call and compact recovery also truncated");
        sequentialAi.CallCount.Should().Be(2);

        task.Status.Should().Be(DevelopmentTaskStatus.Failed);
    }

    [Fact]
    public async Task HandleAsync_NormalOneCallPath_Passes2048TokenBudget()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = new DevelopmentTask
        {
            Id = taskId,
            RepositoryWorkspaceId = _workspaceId,
            Title = "Normal task",
            Description = "Normal description",
            Status = DevelopmentTaskStatus.Draft,
            Priority = DevelopmentTaskPriority.Medium,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var taskRepo = new ConcurrentInMemoryTaskRepository();
        taskRepo.Add(task);
        var analysisRepo = new ConcurrentInMemoryAnalysisRepository();

        var sequentialAi = new SequentialAiProvider(
            new AiResponse
            {
                IsSuccess = true,
                Content = "{\"Summary\":\"Normal summary\",\"Confidence\":90,\"ImpactedFiles\":[{\"FilePath\":\"src/DevPilot.Domain/Entities/DevelopmentTask.cs\",\"ChangeType\":\"Modify\",\"Reason\":\"Update field\"}],\"Dimensions\":[{\"Area\":\"DOMAIN\",\"ImpactLevel\":\"Low\",\"Summary\":\"Entity update\"}],\"ProposedPlan\":[{\"Order\":1,\"Title\":\"Update\",\"Description\":\"Update entity\"}],\"Risks\":[{\"Level\":\"Low\",\"Description\":\"None\"}]}",
                Model = "kimi-k3",
                Provider = "Kimi",
                OutputTokens = 180,
                FinishReason = "stop"
            });

        var handler = new AnalyzeTaskImpactCommandHandler(
            taskRepo,
            new FakeWorkspaceQuery { WorkspaceToReturn = _workspace },
            analysisRepo,
            new FakeRepositoryAnalyzer(),
            sequentialAi,
            new FakeEmbeddingProvider(),
            new FakeSearchService(),
            NullLogger<AnalyzeTaskImpactCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new AnalyzeTaskImpactCommand(taskId), CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        sequentialAi.CallCount.Should().Be(1);
        sequentialAi.RecordedRequests[0].MaxTokens.Should().Be(2048);
    }

    private class SequentialAiProvider : IAiProvider
    {
        private readonly List<AiResponse> _responses;
        private int _index;

        public List<AiRequest> RecordedRequests { get; } = new();
        public int CallCount => RecordedRequests.Count;
        public string ProviderName => "SequentialAi";

        public SequentialAiProvider(params AiResponse[] responses)
        {
            _responses = responses.ToList();
        }

        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            RecordedRequests.Add(request);
            if (_index < _responses.Count)
            {
                var resp = _responses[_index++];
                return Task.FromResult(resp);
            }
            return Task.FromResult(new AiResponse { IsSuccess = false, ErrorMessage = "No more responses configured." });
        }
    }

    private AnalyzeTaskImpactCommandHandler CreateHandler(
        ITaskRepository taskRepository,
        IImpactAnalysisRepository analysisRepository)
    {
        var validAiResponse = new AiResponse
        {
            IsSuccess = true,
            Content = "{\"Summary\":\"Impact summary\",\"Confidence\":88,\"ImpactedFiles\":[{\"FilePath\":\"src/DevPilot.Api/Controllers/TasksController.cs\",\"ChangeType\":\"Modify\",\"Reason\":\"Updated endpoint\",\"Confidence\":90}],\"ProposedPlan\":[{\"Order\":1,\"Title\":\"Update code\",\"Description\":\"Edit controller\",\"RelatedFiles\":[\"src/DevPilot.Api/Controllers/TasksController.cs\"]}],\"SystemImpacts\":[{\"Area\":\"API Surface\",\"ImpactLevel\":\"Medium\",\"Description\":\"Controller update\"}],\"Risks\":[{\"Level\":\"Low\",\"Description\":\"Minor\",\"Mitigation\":\"Unit tests\"}]}",
            Model = "gemini-2.5-pro",
            Provider = "Gemini",
        };

        return new AnalyzeTaskImpactCommandHandler(
            taskRepository,
            new FakeWorkspaceQuery { WorkspaceToReturn = _workspace },
            analysisRepository,
            new FakeRepositoryAnalyzer(),
            new FakeAiProvider { ResponseToReturn = validAiResponse },
            new FakeEmbeddingProvider(),
            new FakeSearchService(),
            NullLogger<AnalyzeTaskImpactCommandHandler>.Instance);
    }

    private class FakeWorkspaceQuery : IRepositoryWorkspaceQuery
    {
        public RepositoryWorkspace? WorkspaceToReturn { get; set; }
        public Task<RepositoryWorkspace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkspaceToReturn);
    }

    private class FakeAiProvider : IAiProvider
    {
        public string ProviderName => "FakeAi";
        public AiResponse ResponseToReturn { get; set; } = new() { IsSuccess = true };

        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(ResponseToReturn);
    }

    private class BlockingAiProvider : IAiProvider
    {
        private readonly TaskCompletionSource<bool> _enteredTcs;
        private readonly Task _releaseTask;
        private readonly AiResponse _responseToReturn;

        public BlockingAiProvider(
            TaskCompletionSource<bool> enteredTcs,
            Task releaseTask,
            AiResponse responseToReturn)
        {
            _enteredTcs = enteredTcs;
            _releaseTask = releaseTask;
            _responseToReturn = responseToReturn;
        }

        public string ProviderName => "BlockingAi";

        public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            _enteredTcs.TrySetResult(true);
            await _releaseTask.ConfigureAwait(false);
            return _responseToReturn;
        }
    }

    private class FakeRepositoryAnalyzer : IRepositoryAnalyzer
    {
        public Task<RepositoryAnalysisResult> AnalyzeAsync(RepositoryAnalysisRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RepositoryAnalysisResult { Success = true });
    }

    private class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public string ProviderName => "Fake";
        public Task<EmbeddingResult> GenerateAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult(new EmbeddingResult { Success = true, ProviderName = "Fake", Embeddings = texts.Select(_ => new float[384]).ToList() });
    }

    private class FakeSearchService : ISemanticSearchService
    {
        public Task<SemanticSearchResult> SearchAsync(SemanticSearchQuery query, float[]? queryEmbedding, CancellationToken cancellationToken = default)
            => Task.FromResult(new SemanticSearchResult { Success = true });
    }

    private sealed class ConcurrentInMemoryTaskRepository : ITaskRepository
    {
        private readonly object _lock = new();
        private readonly Dictionary<Guid, DevelopmentTask> _tasks = new();

        public void Add(DevelopmentTask task)
        {
            lock (_lock)
            {
                _tasks[task.Id] = task;
            }
        }

        public Task<DevelopmentTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_tasks.TryGetValue(id, out var task) ? task : null);
            }
        }

        public Task<DevelopmentTask?> GetByIdWithWorkspaceAsync(Guid id, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_tasks.TryGetValue(id, out var task) ? task : null);
            }
        }

        public Task<IReadOnlyList<DevelopmentTask>> GetByWorkspaceIdAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<DevelopmentTask>>(_tasks.Values.Where(t => t.RepositoryWorkspaceId == workspaceId).ToList());
            }
        }

        public Task AddAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _tasks[task.Id] = task;
                return Task.CompletedTask;
            }
        }

        public Task UpdateAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _tasks[task.Id] = task;
                return Task.CompletedTask;
            }
        }

        public Task DeleteAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _tasks.Remove(task.Id);
                return Task.CompletedTask;
            }
        }

        public Task<IReadOnlyList<DevelopmentTask>> GetAllAsync(DevelopmentTaskQueryFilter filter, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<DevelopmentTask>>(_tasks.Values.ToList());
            }
        }
    }

    private sealed class ConcurrentInMemoryAnalysisRepository : IImpactAnalysisRepository
    {
        private readonly object _lock = new();
        private readonly Dictionary<Guid, TaskImpactAnalysisEntity> _analyses = new();

        public void Add(TaskImpactAnalysisEntity analysis)
        {
            lock (_lock)
            {
                _analyses[analysis.Id] = analysis;
            }
        }

        public Task<TaskImpactAnalysisEntity?> GetLatestByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var latest = _analyses.Values
                    .Where(a => a.DevelopmentTaskId == taskId)
                    .OrderByDescending(a => a.CreatedAt)
                    .FirstOrDefault();

                return Task.FromResult(latest);
            }
        }

        public Task AddAsync(TaskImpactAnalysisEntity analysis, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _analyses[analysis.Id] = analysis;
                return Task.CompletedTask;
            }
        }

        public Task UpdateAsync(TaskImpactAnalysisEntity analysis, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _analyses[analysis.Id] = analysis;
                return Task.CompletedTask;
            }
        }

        public Task<bool> StartAnalysisAtomicAsync(TaskImpactAnalysisEntity analysis, DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                // Atomic unique partial index invariant simulation:
                // IX_TaskImpactAnalyses_ActivePerTask ON TaskImpactAnalyses (DevelopmentTaskId) WHERE Status = 'InProgress'
                var hasActive = _analyses.Values.Any(a => a.DevelopmentTaskId == task.Id && a.Status == ImpactAnalysisStatus.InProgress);
                if (hasActive)
                {
                    return Task.FromResult(false);
                }

                _analyses[analysis.Id] = analysis;
                return Task.FromResult(true);
            }
        }

        public Task<bool> HasActiveAnalysisForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var hasActive = _analyses.Values.Any(a => a.DevelopmentTaskId == taskId && a.Status == ImpactAnalysisStatus.InProgress);
                return Task.FromResult(hasActive);
            }
        }

        public Task<int> ReconcileStaleAnalysesAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var stale = _analyses.Values
                    .Where(a => a.Status == ImpactAnalysisStatus.InProgress && a.CreatedAt < cutoffUtc)
                    .ToList();

                foreach (var a in stale)
                {
                    a.Status = ImpactAnalysisStatus.Failed;
                    a.CompletedAt = DateTime.UtcNow;
                    a.ErrorMessage = "Impact analysis did not complete before the execution timeout.";
                }

                return Task.FromResult(stale.Count);
            }
        }
    }
}
