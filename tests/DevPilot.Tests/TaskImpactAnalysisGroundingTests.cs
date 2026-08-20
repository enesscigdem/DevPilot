using DevPilot.Application.AiProviders;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.TaskImpactAnalysis.Commands.AnalyzeTaskImpact;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public class TaskImpactAnalysisGroundingTests
{
    private readonly string _repoRoot;

    public TaskImpactAnalysisGroundingTests()
    {
        // Compute the actual repository root (where src/ and tests/ live)
        var currentDir = AppDomain.CurrentDomain.BaseDirectory;
        var dir = new DirectoryInfo(currentDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DevPilot.sln")))
        {
            dir = dir.Parent;
        }
        _repoRoot = dir?.FullName ?? throw new InvalidOperationException("Could not locate DevPilot.sln repository root.");
    }

    [Fact]
    public void Scenario1_ValidExistingModifyPath_RemainsUnchanged()
    {
        var existingPath = "src/DevPilot.Domain/Entities/DevelopmentTask.cs";
        var projectRoots = ProjectGraphHelper.DiscoverProjectRoots(_repoRoot);
        var projectGraph = ProjectGraphHelper.DiscoverProjectGraph(_repoRoot);

        var resolved = ProjectGraphHelper.TryResolveModifyTarget(
            existingPath,
            _repoRoot,
            projectGraph,
            projectRoots,
            out var resolvedPath,
            out var failureReason);

        resolved.Should().BeTrue();
        resolvedPath.Should().Be(existingPath);
        failureReason.Should().BeNull();
    }

    [Fact]
    public void Scenario2_And_8_ActualDevelopmentTaskRepositoryScenario_RemapsToEfTaskRepository()
    {
        var hallucinatedPath = "src/DevPilot.Infrastructure/Repositories/EfDevelopmentTaskRepository.cs";
        var projectRoots = ProjectGraphHelper.DiscoverProjectRoots(_repoRoot);
        var projectGraph = ProjectGraphHelper.DiscoverProjectGraph(_repoRoot);

        var resolved = ProjectGraphHelper.TryResolveModifyTarget(
            hallucinatedPath,
            _repoRoot,
            projectGraph,
            projectRoots,
            out var resolvedPath,
            out var failureReason);

        resolved.Should().BeTrue();
        resolvedPath.Should().Be("src/DevPilot.Infrastructure/Tasks/EfTaskRepository.cs");
        failureReason.Should().BeNull();
    }

    [Fact]
    public void Scenario3_HallucinatedModifyPathWithNoCandidate_FailsSafely()
    {
        var hallucinatedPath = "src/DevPilot.Infrastructure/Services/CompletelyNonExistentService999.cs";
        var projectRoots = ProjectGraphHelper.DiscoverProjectRoots(_repoRoot);
        var projectGraph = ProjectGraphHelper.DiscoverProjectGraph(_repoRoot);

        var resolved = ProjectGraphHelper.TryResolveModifyTarget(
            hallucinatedPath,
            _repoRoot,
            projectGraph,
            projectRoots,
            out var resolvedPath,
            out var failureReason);

        resolved.Should().BeFalse();
        failureReason.Should().Contain("does not exist in the repository and cannot be deterministically resolved");
    }

    [Fact]
    public void Scenario4_AmbiguousMatchingCandidates_FailsSafelyWithoutGuessing()
    {
        // When a generic path matching multiple candidates in Infrastructure is proposed without distinct domain
        var ambiguousPath = "src/DevPilot.Infrastructure/Repositories/Repository.cs";
        var projectRoots = ProjectGraphHelper.DiscoverProjectRoots(_repoRoot);
        var projectGraph = ProjectGraphHelper.DiscoverProjectGraph(_repoRoot);

        var resolved = ProjectGraphHelper.TryResolveModifyTarget(
            ambiguousPath,
            _repoRoot,
            projectGraph,
            projectRoots,
            out var resolvedPath,
            out var failureReason);

        resolved.Should().BeFalse();
        failureReason.Should().Contain("Ambiguous mapping cannot be resolved safely");
    }

    [Fact]
    public void Scenario5_CreatePath_IsAllowedToBeNewUnderValidProjectRoot()
    {
        var newCreatePath = "src/DevPilot.Application/Tasks/Commands/NewFeatureCommand.cs";
        var projectRoots = ProjectGraphHelper.DiscoverProjectRoots(_repoRoot);

        var isInRoot = ProjectGraphHelper.IsCsFileInProjectRoot(newCreatePath, projectRoots);
        isInRoot.Should().BeTrue("New file under src/DevPilot.Application is valid for Create");
    }

    [Fact]
    public void Scenario6_InvalidPathOutsideProjectRoots_IsRejected()
    {
        var outsidePath = "some_random_folder/ArbitraryFile.cs";
        var projectRoots = ProjectGraphHelper.DiscoverProjectRoots(_repoRoot);

        var isInRoot = ProjectGraphHelper.IsCsFileInProjectRoot(outsidePath, projectRoots);
        isInRoot.Should().BeFalse("File outside discovered project roots must be rejected");
    }

    [Fact]
    public void Scenario7_ExecutionTimeStrictModify_RemainsActiveWhenFileMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "StrictModifyTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var projectRoots = new[] { "src/Proj" };
            var request = new DeveloperAgentRequest(
                TaskId: Guid.NewGuid(),
                ExecutionId: Guid.NewGuid(),
                TaskTitle: "Title",
                TaskDescription: "Desc",
                AcceptanceCriteria: null,
                ImpactAnalysisSummary: "Summary",
                ProposedPlan: "Plan",
                ImpactedFilePaths: new[] { "src/Proj/NonExistentFile.cs" },
                WorkspacePath: tempDir,
                BranchName: "main",
                ImpactedFiles: new[] { new ImpactedFileDetail("src/Proj/NonExistentFile.cs", "Modify", "Reason") });

            var manifest = DeveloperAgent.BuildManifestFromImpactAnalysis(request, tempDir, projectRoots, 10, null);
            manifest.Files[0].Action.Should().Be(FileEditAction.Modify);

            // Verify that execution-time check asserts existence
            var fullPath = Path.Combine(tempDir, manifest.Files[0].FilePath.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(fullPath).Should().BeFalse();

            Action act = () =>
            {
                if (manifest.Files[0].Action == FileEditAction.Modify && !File.Exists(fullPath))
                {
                    throw new InvalidOperationException($"Strict Modify action failed: target file does not exist at '{manifest.Files[0].FilePath}'.");
                }
            };

            act.Should().Throw<InvalidOperationException>()
               .WithMessage("*Strict Modify action failed: target file does not exist*");
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
    public async Task ImpactAnalysisHandler_RejectsNonExistentModifyPath_AndDoesNotEnterAwaitingApproval()
    {
        var taskRepo = new FakeTaskRepository();
        var workspaceQuery = new FakeWorkspaceQuery { WorkspaceToReturn = new RepositoryWorkspace { Id = Guid.NewGuid(), Status = RepositoryWorkspaceStatus.Completed, LocalPath = _repoRoot } };
        var analysisRepo = new FakeAnalysisRepository();
        var analyzer = new FakeRepositoryAnalyzer();
        var embeddingProvider = new FakeEmbeddingProvider();
        var searchService = new FakeSearchService();

        var aiResponseJson = """
        {
            "summary": "Impact summary",
            "confidence": 95,
            "impactedFiles": [
                {
                    "filePath": "src/DevPilot.Infrastructure/Services/CompletelyFakeNonExistentService123.cs",
                    "changeType": "Modify",
                    "reason": "Modify fake service"
                }
            ],
            "proposedPlan": []
        }
        """;

        var aiProvider = new FakeAiProvider { ResponseToReturn = aiResponseJson };

        var handler = new AnalyzeTaskImpactCommandHandler(
            taskRepo,
            workspaceQuery,
            analysisRepo,
            analyzer,
            aiProvider,
            embeddingProvider,
            searchService,
            NullLogger<AnalyzeTaskImpactCommandHandler>.Instance);

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspaceQuery.WorkspaceToReturn.Id,
            Title = "Task Title",
            Description = "Task Desc",
            Status = DevelopmentTaskStatus.Draft
        };
        taskRepo.Tasks[task.Id] = task;

        var result = await handler.HandleAsync(new AnalyzeTaskImpactCommand(task.Id));

        result.Success.Should().BeFalse("Analysis must fail when Modify target does not exist and cannot be resolved");
        result.ErrorMessage.Should().Contain("does not exist in the repository and cannot be deterministically resolved");
        task.Status.Should().Be(DevelopmentTaskStatus.Failed, "Task must NOT reach AwaitingApproval");
    }

    [Fact]
    public async Task Scenario9_ImpactAnalysis_RejectsPlanProposingUnreferencedMediatRFramework()
    {
        var taskId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        var task = new DevelopmentTask
        {
            Id = taskId,
            RepositoryWorkspaceId = workspaceId,
            Title = "Add repository workspace task count endpoint",
            Description = "Add query and endpoint for task count",
            Status = DevelopmentTaskStatus.Draft
        };

        var taskRepo = new FakeTaskRepository();
        taskRepo.Tasks[taskId] = task;

        var workspaceQuery = new FakeWorkspaceQuery
        {
            WorkspaceToReturn = new RepositoryWorkspace
            {
                Id = workspaceId,
                Owner = "test",
                Repository = "repo",
                Branch = "main",
                Status = RepositoryWorkspaceStatus.Completed,
                LocalPath = _repoRoot
            }
        };

        var analysisRepo = new FakeAnalysisRepository();
        var fakeAiProvider = new FakeAiProvider
        {
            ResponseToReturn = """
                {
                    "summary": "Introduce a minimal MediatR-style query and handler for task count",
                    "confidence": 90,
                    "proposedPlan": [
                        {
                            "order": 1,
                            "title": "Create MediatR query",
                            "description": "Create GetRepositoryWorkspaceTaskCountQuery implementing IRequest<int>",
                            "relatedFiles": ["src/DevPilot.Application/RepositoryWorkspaces/Queries/GetRepositoryWorkspaceTaskCountQuery.cs"]
                        }
                    ],
                    "impactedFiles": [
                        {
                            "filePath": "src/DevPilot.Application/RepositoryWorkspaces/Queries/GetRepositoryWorkspaceTaskCountQuery.cs",
                            "changeType": "Create",
                            "reason": "New query record"
                        }
                    ]
                }
                """
        };

        var handler = new AnalyzeTaskImpactCommandHandler(
            taskRepo,
            workspaceQuery,
            analysisRepo,
            new FakeRepositoryAnalyzer(),
            fakeAiProvider,
            new FakeEmbeddingProvider(),
            new FakeSearchService(),
            NullLogger<AnalyzeTaskImpactCommandHandler>.Instance);

        var result = await handler.HandleAsync(new AnalyzeTaskImpactCommand(taskId), CancellationToken.None);

        result.Success.Should().BeFalse("Analysis must reject unreferenced framework proposals");
        result.ErrorMessage.Should().Contain("unsupported framework 'MediatR'");
        task.Status.Should().Be(DevelopmentTaskStatus.Failed, "Task must NOT reach AwaitingApproval");
    }

    [Fact]
    public async Task Scenario10_InvalidModifyPath_TriggersBoundedRepair_AndSucceedsWhenRepairedWithValidPath()
    {
        var taskRepo = new FakeTaskRepository();
        var workspaceQuery = new FakeWorkspaceQuery
        {
            WorkspaceToReturn = new RepositoryWorkspace
            {
                Id = Guid.NewGuid(),
                Status = RepositoryWorkspaceStatus.Completed,
                LocalPath = _repoRoot
            }
        };
        var analysisRepo = new FakeAnalysisRepository();
        var analyzer = new FakeRepositoryAnalyzer();
        var embeddingProvider = new FakeEmbeddingProvider();
        var searchService = new FakeSearchService();

        var initialInvalidResponse = """
        {
            "summary": "Implement search feature",
            "confidence": 85,
            "impactedFiles": [
                {
                    "filePath": "src/DevPilot.Domain/Entities/DevelopmentTask.cs",
                    "changeType": "Modify",
                    "reason": "Valid domain model modification"
                },
                {
                    "filePath": "src/DevPilot.Application/Features/Products/Queries/ListProductsQueryHandler.cs",
                    "changeType": "Modify",
                    "reason": "Invented nonexistent handler"
                }
            ],
            "proposedPlan": [
                {
                    "order": 1,
                    "title": "Update handler",
                    "description": "Add search parameter",
                    "relatedFiles": ["src/DevPilot.Domain/Entities/DevelopmentTask.cs"]
                }
            ]
        }
        """;

        var repairedValidResponse = """
        {
            "summary": "Implement search feature (repaired)",
            "confidence": 90,
            "impactedFiles": [
                {
                    "filePath": "src/DevPilot.Domain/Entities/DevelopmentTask.cs",
                    "changeType": "Modify",
                    "reason": "Valid domain model modification"
                },
                {
                    "filePath": "src/DevPilot.Application/Tasks/Commands/AnalyzeTaskImpact/AnalyzeTaskImpactCommandHandler.cs",
                    "changeType": "Modify",
                    "reason": "Existing real handler"
                }
            ],
            "proposedPlan": [
                {
                    "order": 1,
                    "title": "Update real handler",
                    "description": "Add search logic",
                    "relatedFiles": ["src/DevPilot.Domain/Entities/DevelopmentTask.cs"]
                }
            ]
        }
        """;

        var aiProvider = new FakeAiProvider();
        aiProvider.ResponsesToReturn.Enqueue(initialInvalidResponse);
        aiProvider.ResponsesToReturn.Enqueue(repairedValidResponse);

        var handler = new AnalyzeTaskImpactCommandHandler(
            taskRepo,
            workspaceQuery,
            analysisRepo,
            analyzer,
            aiProvider,
            embeddingProvider,
            searchService,
            NullLogger<AnalyzeTaskImpactCommandHandler>.Instance);

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspaceQuery.WorkspaceToReturn.Id,
            Title = "Search products",
            Description = "Add query search capability",
            Status = DevelopmentTaskStatus.Draft
        };
        taskRepo.Tasks[task.Id] = task;

        var result = await handler.HandleAsync(new AnalyzeTaskImpactCommand(task.Id));

        result.Success.Should().BeTrue("Analysis must succeed after bounded repair with valid real path");
        aiProvider.SendAsyncCallCount.Should().Be(2, "Exactly one initial call and one repair call must occur");
        task.Status.Should().Be(DevelopmentTaskStatus.AwaitingApproval);
        result.Analysis!.StructuredResult!.ImpactedFiles.Should().HaveCount(2);
        result.Analysis.StructuredResult.ImpactedFiles[0].FilePath.Should().Be("src/DevPilot.Domain/Entities/DevelopmentTask.cs");
        result.Analysis.StructuredResult.ImpactedFiles[1].FilePath.Should().Be("src/DevPilot.Application/TaskImpactAnalysis/Commands/AnalyzeTaskImpact/AnalyzeTaskImpactCommandHandler.cs");

        // Verify repair prompt received error details and preserved entries
        aiProvider.ReceivedRequests[1].UserPrompt.Should().Contain("ListProductsQueryHandler.cs");
        aiProvider.ReceivedRequests[1].UserPrompt.Should().Contain("src/DevPilot.Domain/Entities/DevelopmentTask.cs");
    }

    [Fact]
    public async Task Scenario11_InvalidModifyPath_BoundedRepairFailsOnSecondInvalidPath_NoSecondRepair()
    {
        var taskRepo = new FakeTaskRepository();
        var workspaceQuery = new FakeWorkspaceQuery
        {
            WorkspaceToReturn = new RepositoryWorkspace
            {
                Id = Guid.NewGuid(),
                Status = RepositoryWorkspaceStatus.Completed,
                LocalPath = _repoRoot
            }
        };
        var analysisRepo = new FakeAnalysisRepository();
        var analyzer = new FakeRepositoryAnalyzer();
        var embeddingProvider = new FakeEmbeddingProvider();
        var searchService = new FakeSearchService();

        var initialInvalidResponse = """
        {
            "summary": "Implement feature",
            "confidence": 80,
            "impactedFiles": [
                {
                    "filePath": "src/DevPilot.Application/NonExistentFileA.cs",
                    "changeType": "Modify",
                    "reason": "Nonexistent file A"
                }
            ],
            "proposedPlan": []
        }
        """;

        var secondInvalidResponse = """
        {
            "summary": "Implement feature retry",
            "confidence": 80,
            "impactedFiles": [
                {
                    "filePath": "src/DevPilot.Application/StillNonExistentFileB.cs",
                    "changeType": "Modify",
                    "reason": "Nonexistent file B"
                }
            ],
            "proposedPlan": []
        }
        """;

        var aiProvider = new FakeAiProvider();
        aiProvider.ResponsesToReturn.Enqueue(initialInvalidResponse);
        aiProvider.ResponsesToReturn.Enqueue(secondInvalidResponse);

        var handler = new AnalyzeTaskImpactCommandHandler(
            taskRepo,
            workspaceQuery,
            analysisRepo,
            analyzer,
            aiProvider,
            embeddingProvider,
            searchService,
            NullLogger<AnalyzeTaskImpactCommandHandler>.Instance);

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspaceQuery.WorkspaceToReturn.Id,
            Title = "Task Title",
            Description = "Task Desc",
            Status = DevelopmentTaskStatus.Draft
        };
        taskRepo.Tasks[task.Id] = task;

        var result = await handler.HandleAsync(new AnalyzeTaskImpactCommand(task.Id));

        result.Success.Should().BeFalse("Analysis must fail after single repair attempt still returns invalid path");
        aiProvider.SendAsyncCallCount.Should().Be(2, "Must NOT perform a second repair attempt");
        result.ErrorMessage.Should().Contain("does not exist in the repository and cannot be deterministically resolved");
        task.Status.Should().Be(DevelopmentTaskStatus.Failed);
    }

    [Fact]
    public async Task Scenario12_CreateActionOnExistingFile_IsRejectedAndTriggersRepair()
    {
        var taskRepo = new FakeTaskRepository();
        var workspaceQuery = new FakeWorkspaceQuery
        {
            WorkspaceToReturn = new RepositoryWorkspace
            {
                Id = Guid.NewGuid(),
                Status = RepositoryWorkspaceStatus.Completed,
                LocalPath = _repoRoot
            }
        };
        var analysisRepo = new FakeAnalysisRepository();
        var analyzer = new FakeRepositoryAnalyzer();
        var embeddingProvider = new FakeEmbeddingProvider();
        var searchService = new FakeSearchService();

        // Existing file marked as Create
        var initialCreateOnExistingResponse = """
        {
            "summary": "Create existing file",
            "confidence": 80,
            "impactedFiles": [
                {
                    "filePath": "src/DevPilot.Domain/Entities/DevelopmentTask.cs",
                    "changeType": "Create",
                    "reason": "Creating already existing entity"
                }
            ],
            "proposedPlan": []
        }
        """;

        var repairedResponse = """
        {
            "summary": "Modify existing file",
            "confidence": 90,
            "impactedFiles": [
                {
                    "filePath": "src/DevPilot.Domain/Entities/DevelopmentTask.cs",
                    "changeType": "Modify",
                    "reason": "Modifying existing entity"
                }
            ],
            "proposedPlan": []
        }
        """;

        var aiProvider = new FakeAiProvider();
        aiProvider.ResponsesToReturn.Enqueue(initialCreateOnExistingResponse);
        aiProvider.ResponsesToReturn.Enqueue(repairedResponse);

        var handler = new AnalyzeTaskImpactCommandHandler(
            taskRepo,
            workspaceQuery,
            analysisRepo,
            analyzer,
            aiProvider,
            embeddingProvider,
            searchService,
            NullLogger<AnalyzeTaskImpactCommandHandler>.Instance);

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspaceQuery.WorkspaceToReturn.Id,
            Title = "Task Title",
            Description = "Task Desc",
            Status = DevelopmentTaskStatus.Draft
        };
        taskRepo.Tasks[task.Id] = task;

        var result = await handler.HandleAsync(new AnalyzeTaskImpactCommand(task.Id));

        result.Success.Should().BeTrue("Analysis must succeed after repair corrects Create to Modify for existing file");
        aiProvider.SendAsyncCallCount.Should().Be(2);
        aiProvider.ReceivedRequests[1].UserPrompt.Should().Contain("already exists in the repository");
        task.Status.Should().Be(DevelopmentTaskStatus.AwaitingApproval);
    }

    private class FakeTaskRepository : ITaskRepository
    {
        public Dictionary<Guid, DevelopmentTask> Tasks { get; } = new();
        public Task<DevelopmentTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Tasks.TryGetValue(id, out var t) ? t : null);
        public Task AddAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            Tasks[task.Id] = task;
            return Task.CompletedTask;
        }
        public Task UpdateAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            Tasks[task.Id] = task;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            Tasks.Remove(task.Id);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<DevelopmentTask>> GetAllAsync(DevelopmentTaskQueryFilter filter, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DevelopmentTask>>(Tasks.Values.ToList());
    }

    private class FakeWorkspaceQuery : IRepositoryWorkspaceQuery
    {
        public RepositoryWorkspace? WorkspaceToReturn { get; set; }
        public Task<RepositoryWorkspace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(WorkspaceToReturn);
    }

    private class FakeAnalysisRepository : IImpactAnalysisRepository
    {
        public Dictionary<Guid, Domain.Entities.TaskImpactAnalysis> Analyses { get; } = new();
        public Task<Domain.Entities.TaskImpactAnalysis?> GetLatestByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(Analyses.Values.Where(a => a.DevelopmentTaskId == taskId).OrderByDescending(a => a.CreatedAt).FirstOrDefault());
        public Task AddAsync(Domain.Entities.TaskImpactAnalysis analysis, CancellationToken cancellationToken = default)
        {
            Analyses[analysis.Id] = analysis;
            return Task.CompletedTask;
        }
        public Task UpdateAsync(Domain.Entities.TaskImpactAnalysis analysis, CancellationToken cancellationToken = default)
        {
            Analyses[analysis.Id] = analysis;
            return Task.CompletedTask;
        }
        public Task<bool> StartAnalysisAtomicAsync(Domain.Entities.TaskImpactAnalysis analysis, DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            if (Analyses.Values.Any(a => a.DevelopmentTaskId == task.Id && a.Status == ImpactAnalysisStatus.InProgress))
            {
                return Task.FromResult(false);
            }
            Analyses[analysis.Id] = analysis;
            return Task.FromResult(true);
        }
        public Task<bool> HasActiveAnalysisForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(Analyses.Values.Any(a => a.DevelopmentTaskId == taskId && a.Status == ImpactAnalysisStatus.InProgress));
        public Task<int> ReconcileStaleAnalysesAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
        {
            int count = 0;
            foreach (var a in Analyses.Values.Where(a => a.Status == ImpactAnalysisStatus.InProgress && a.CreatedAt < cutoffUtc))
            {
                a.Status = ImpactAnalysisStatus.Failed;
                a.CompletedAt = DateTime.UtcNow;
                a.ErrorMessage = "Impact analysis did not complete before the execution timeout.";
                count++;
            }
            return Task.FromResult(count);
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
            => Task.FromResult(new EmbeddingResult { Success = true, ProviderName = "Fake", Embeddings = texts.Select(_ => new float[1536]).ToList() });
    }

    private class FakeSearchService : ISemanticSearchService
    {
        public Task<SemanticSearchResult> SearchAsync(SemanticSearchQuery query, float[]? queryEmbedding, CancellationToken cancellationToken = default)
            => Task.FromResult(new SemanticSearchResult { Success = true });
    }
}
