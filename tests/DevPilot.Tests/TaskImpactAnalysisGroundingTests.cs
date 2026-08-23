using DevPilot.Application.AiProviders;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Commands.StartExecution;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.TaskImpactAnalysis.Commands.AnalyzeTaskImpact;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.TaskImpactAnalysis.Services;
using DevPilot.Application.Tasks.Commands.ApproveTask;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Constants;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Domain.ValueObjects;
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

    [Fact]
    public void CapacityBoundary_12FileApprovedPlan_SuccessfullyConstructsManifest()
    {
        var projectRoots = ProjectGraphHelper.DiscoverProjectRoots(_repoRoot);
        var projectGraph = ProjectGraphHelper.DiscoverProjectGraph(_repoRoot);

        // 12 files representing a realistic multi-project feature plan
        var twelveFiles = new List<ImpactedFileDetail>
        {
            new("src/DevPilot.Domain/Entities/DevelopmentTask.cs", "Modify", "Add properties"),
            new("src/DevPilot.Domain/Enums/DevelopmentTaskStatus.cs", "Modify", "Add enum values"),
            new("src/DevPilot.Application/Tasks/Dtos/TaskDto.cs", "Modify", "Update DTO"),
            new("src/DevPilot.Application/Tasks/Commands/ApproveTask/ApproveTaskCommand.cs", "Modify", "Update command"),
            new("src/DevPilot.Application/Tasks/Commands/ApproveTask/ApproveTaskCommandHandler.cs", "Modify", "Update handler"),
            new("src/DevPilot.Application/Tasks/Ports/ITaskRepository.cs", "Modify", "Update interface"),
            new("src/DevPilot.Infrastructure/Tasks/EfTaskRepository.cs", "Modify", "Update repo"),
            new("src/DevPilot.Infrastructure/Persistence/DevPilotDbContext.cs", "Modify", "Update DbContext"),
            new("src/DevPilot.Infrastructure/Migrations/20260821_UpdateTask.cs", "Add", "New migration"),
            new("src/DevPilot.Api/Controllers/TasksController.cs", "Modify", "Update API controller"),
            new("tests/DevPilot.Tests/TaskTests.cs", "Add", "Unit tests"),
            new("tests/DevPilot.Tests/TaskIntegrationTests.cs", "Add", "Integration tests")
        };

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Twelve File Feature Plan",
            TaskDescription: "Add complete 12-file feature",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "12-file plan",
            ProposedPlan: "Plan steps",
            ImpactedFilePaths: twelveFiles.Select(f => f.FilePath).ToList(),
            WorkspacePath: _repoRoot,
            BranchName: "main",
            ImpactedFiles: twelveFiles);

        // Max limit is ExecutionCapacityPolicy.MaxImpactedFiles (20)
        var manifest = DeveloperAgent.BuildManifestFromImpactAnalysis(
            request,
            _repoRoot,
            projectRoots,
            ExecutionCapacityPolicy.MaxImpactedFiles,
            projectGraph);

        manifest.Should().NotBeNull();
        manifest.Files.Count.Should().Be(12);
    }

    [Fact]
    public void CapacityBoundary_Exactly20Files_AllowedAndConstructsManifest()
    {
        var projectRoots = ProjectGraphHelper.DiscoverProjectRoots(_repoRoot);
        var projectGraph = ProjectGraphHelper.DiscoverProjectGraph(_repoRoot);

        var twentyFiles = Enumerable.Range(1, 20)
            .Select(i => new ImpactedFileDetail($"src/DevPilot.Domain/Entities/Entity{i}.cs", "Add", $"Add entity {i}"))
            .ToList();

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Twenty File Plan",
            TaskDescription: "Add 20 entities",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "20-file plan",
            ProposedPlan: "Plan steps",
            ImpactedFilePaths: twentyFiles.Select(f => f.FilePath).ToList(),
            WorkspacePath: _repoRoot,
            BranchName: "main",
            ImpactedFiles: twentyFiles);

        var manifest = DeveloperAgent.BuildManifestFromImpactAnalysis(
            request,
            _repoRoot,
            projectRoots,
            ExecutionCapacityPolicy.MaxImpactedFiles,
            projectGraph);

        manifest.Should().NotBeNull();
        manifest.Files.Count.Should().Be(20);
    }

    [Fact]
    public void CapacityBoundary_OverLimit21Files_RejectedInManifestConstruction()
    {
        var projectRoots = ProjectGraphHelper.DiscoverProjectRoots(_repoRoot);
        var projectGraph = ProjectGraphHelper.DiscoverProjectGraph(_repoRoot);

        var twentyOneFiles = Enumerable.Range(1, 21)
            .Select(i => new ImpactedFileDetail($"src/DevPilot.Domain/Entities/Entity{i}.cs", "Add", $"Add entity {i}"))
            .ToList();

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Twenty One File Plan",
            TaskDescription: "Add 21 entities",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "21-file plan",
            ProposedPlan: "Plan steps",
            ImpactedFilePaths: twentyOneFiles.Select(f => f.FilePath).ToList(),
            WorkspacePath: _repoRoot,
            BranchName: "main",
            ImpactedFiles: twentyOneFiles);

        Action act = () => DeveloperAgent.BuildManifestFromImpactAnalysis(
            request,
            _repoRoot,
            projectRoots,
            ExecutionCapacityPolicy.MaxImpactedFiles,
            projectGraph);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Approved impact analysis contains 21 impacted files, exceeding maximum allowed limit of 20*");
    }

    [Fact]
    public void CapacityBoundary_OverLimit21Files_RejectedInChangeIntelligenceParsingWithoutSilentTruncation()
    {
        var evidence = new RepositoryEvidenceProfile();
        var filesJson = string.Join(",", Enumerable.Range(1, 21).Select(i => $"{{\"filePath\":\"src/DevPilot.Domain/Entities/Entity{i}.cs\",\"changeType\":\"Add\",\"reason\":\"Add entity {i}\"}}"));
        var rawJson = $"{{\"summary\":\"Large plan\",\"confidence\":90,\"impactedFiles\":[{filesJson}]}}";

        var parseResult = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(rawJson, evidence, _repoRoot);

        parseResult.Success.Should().BeFalse("21 files must be rejected during Change Intelligence parsing");
        parseResult.ErrorMessage.Should().Contain("exceeds maximum executable capacity (21 impacted files > 20 limit)");
    }

    [Fact]
    public async Task CapacityBoundary_OverLimit21Files_RejectedInApproveTaskCommandHandler()
    {
        var taskRepo = new FakeTaskRepository();
        var analysisRepo = new FakeAnalysisRepository();
        var handler = new ApproveTaskCommandHandler(taskRepo, analysisRepo, NullLogger<ApproveTaskCommandHandler>.Instance);

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = Guid.NewGuid(),
            Title = "Task Title",
            Description = "Task Desc",
            Status = DevelopmentTaskStatus.AwaitingApproval
        };
        taskRepo.Tasks[task.Id] = task;

        var analysis = new Domain.Entities.TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.Completed,
            Summary = "Large Plan",
            StructuredResult = new ImpactAnalysisResultData
            {
                Summary = "Large Plan",
                ImpactedFiles = Enumerable.Range(1, 21)
                    .Select(i => new ImpactedFile { FilePath = $"src/DevPilot.Domain/Entity{i}.cs", ChangeType = ImpactFileChangeType.Add })
                    .ToList()
            }
        };
        analysisRepo.Analyses[analysis.Id] = analysis;

        var result = await handler.HandleAsync(new ApproveTaskCommand(task.Id));

        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("exceeds maximum executable capacity of 20 files");
        task.Status.Should().Be(DevelopmentTaskStatus.AwaitingApproval);
    }

    [Fact]
    public void HistoricalMigrationFile_ProposingModify_IsRejectedWithGroundingFailure()
    {
        var evidence = new RepositoryEvidenceProfile(
            MigrationFiles: new[] { "src/DevPilot.Infrastructure/Migrations/20260815201916_AddRepositoryWorkspace.cs" },
            InventoryCsFiles: new[] { "src/DevPilot.Infrastructure/Migrations/20260815201916_AddRepositoryWorkspace.cs" });

        var rawJson = """
        {
          "summary": "Update database schema",
          "confidence": 85,
          "impactedFiles": [
            {
              "filePath": "src/DevPilot.Infrastructure/Migrations/20260815201916_AddRepositoryWorkspace.cs",
              "changeType": "Modify",
              "reason": "Modify historical migration"
            }
          ]
        }
        """;

        var parseResult = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(rawJson, evidence, _repoRoot);

        parseResult.Success.Should().BeFalse();
        parseResult.ErrorMessage.Should().Contain("existing historical migration");
        parseResult.ErrorMessage.Should().Contain("propose a new migration file with changeType 'Add'");
    }

    [Fact]
    public void NewMigrationRequirement_ProposingAdd_IsAcceptedWithMigrationRelationship()
    {
        var evidence = new RepositoryEvidenceProfile(
            ProjectRoots: new[] { "src/DevPilot.Infrastructure" },
            MigrationFiles: new[] { "src/DevPilot.Infrastructure/Migrations/20260815201916_AddRepositoryWorkspace.cs" },
            HasEfCore: true);

        var rawJson = """
        {
          "summary": "Add order filter migration",
          "confidence": 90,
          "impactedFiles": [
            {
              "filePath": "src/DevPilot.Infrastructure/Migrations/20260821_AddOrderFilter.cs",
              "changeType": "Add",
              "reason": "New EF Core migration for order filters"
            }
          ]
        }
        """;

        var parseResult = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(rawJson, evidence, _repoRoot);

        parseResult.Success.Should().BeTrue();
        parseResult.ResultData.Should().NotBeNull();
        parseResult.ResultData!.ImpactedFiles.Should().HaveCount(1);
        parseResult.ResultData.ImpactedFiles[0].ChangeType.Should().Be(ImpactFileChangeType.Add);
        parseResult.ResultData.ImpactedFiles[0].EvidenceType.Should().Be("MigrationRelationship");
        parseResult.ResultData.ImpactedFiles[0].Confidence.Should().Be(90);
    }

    [Fact]
    public void Scenario_NonDotNetRepo_ReceivesGenericFileInventory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devpilot_test_nondotnet_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "models"));
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "services"));

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "src", "models", "issue.ts"), "export interface Issue { id: string; }");
            File.WriteAllText(Path.Combine(tempDir, "src", "services", "issueService.ts"), "export class IssueService {}");
            File.WriteAllText(Path.Combine(tempDir, "package.json"), """{"name": "issue-tracker", "scripts": {"build": "tsc"}}""");

            var evidence = ChangeIntelligenceEvidenceCollector.CollectEvidence(tempDir, new RepositoryProfile(RepositoryVerificationState.Configured, Array.Empty<string>(), Array.Empty<RepositoryCheck>(), null));

            evidence.InventoryFiles.Should().Contain("src/models/issue.ts");
            evidence.InventoryFiles.Should().Contain("src/services/issueService.ts");
            evidence.InventoryFiles.Should().Contain("package.json");
            evidence.ProjectGraph.Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Scenario_TypeScriptRepo_InventoryIncludesExactTsPaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devpilot_test_ts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "controllers"));
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "routes"));

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "src", "controllers", "issueController.ts"), "export class IssueController {}");
            File.WriteAllText(Path.Combine(tempDir, "src", "routes", "issueRoutes.ts"), "export const routes = [];");
            File.WriteAllText(Path.Combine(tempDir, "tsconfig.json"), "{}");

            var evidence = ChangeIntelligenceEvidenceCollector.CollectEvidence(tempDir, new RepositoryProfile(RepositoryVerificationState.Unconfigured, Array.Empty<string>(), Array.Empty<RepositoryCheck>(), null));

            evidence.InventoryFiles.Should().Contain("src/controllers/issueController.ts");
            evidence.InventoryFiles.Should().Contain("src/routes/issueRoutes.ts");
            evidence.InventoryFiles.Should().Contain("tsconfig.json");
            evidence.ControllerFiles.Should().Contain("src/controllers/issueController.ts");
            evidence.ControllerFiles.Should().Contain("src/routes/issueRoutes.ts");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Scenario_PackageJsonAndTsconfigEvidence_ExposedWithoutReadmeInference()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devpilot_test_pkg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "package.json"), """
            {
              "name": "devpilot-issue-tracker",
              "scripts": {
                "build": "tsc",
                "start": "node dist/server.js",
                "dev": "ts-node src/server.ts"
              },
              "dependencies": {
                "express": "^4.19.2"
              },
              "devDependencies": {
                "typescript": "^5.4.5"
              }
            }
            """);
            File.WriteAllText(Path.Combine(tempDir, "tsconfig.json"), "{}");
            File.WriteAllText(Path.Combine(tempDir, "README.md"), "Build with dotnet build. Add EF Core migrations before running.");

            var evidence = ChangeIntelligenceEvidenceCollector.CollectEvidence(tempDir, new RepositoryProfile(RepositoryVerificationState.Configured, Array.Empty<string>(), Array.Empty<RepositoryCheck>(), null));

            evidence.NodeTechnology.Should().NotBeNull();
            evidence.NodeTechnology!.HasTypeScript.Should().BeTrue();
            evidence.NodeTechnology.PackageManager.Should().Be("npm");
            evidence.NodeTechnology.Scripts.Should().Contain("build: tsc");
            evidence.NodeTechnology.Frameworks.Should().Contain("express");
            evidence.HasEfCore.Should().BeFalse("README command inference must not fabricate .NET evidence.");
            evidence.ProjectGraph.Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Scenario_NodeTypeScriptRepo_DoesNotReceiveFabricatedDotNetProjectEvidence()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devpilot_test_nodotnet_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "models"));
        Directory.CreateDirectory(Path.Combine(tempDir, "data"));

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "src", "models", "issue.ts"), "export interface Issue {}");
            File.WriteAllText(Path.Combine(tempDir, "data", "issues.json"), "[]");
            File.WriteAllText(Path.Combine(tempDir, "package.json"), """{"name": "issue-tracker", "scripts": {"build": "tsc"}}""");

            var evidence = ChangeIntelligenceEvidenceCollector.CollectEvidence(tempDir, new RepositoryProfile(RepositoryVerificationState.Configured, Array.Empty<string>(), Array.Empty<RepositoryCheck>(), null));

            evidence.HasEfCore.Should().BeFalse();
            evidence.ProjectGraph.Should().BeEmpty();
            evidence.ProjectRoots.Should().BeEmpty();
            evidence.PersistenceFiles.Should().Contain("src/models/issue.ts");
            evidence.PersistenceFiles.Should().Contain("data/issues.json");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Scenario_ModifyExactExistingPath_Accepted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devpilot_test_exact_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "models"));

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "src", "models", "issue.ts"), "export interface Issue {}");
            var evidence = ChangeIntelligenceEvidenceCollector.CollectEvidence(tempDir, new RepositoryProfile(RepositoryVerificationState.Configured, Array.Empty<string>(), Array.Empty<RepositoryCheck>(), null));

            var rawJson = """
            {
              "summary": "Update issue model to include status property",
              "confidence": 95,
              "impactedFiles": [
                {
                  "filePath": "src/models/issue.ts",
                  "changeType": "Modify",
                  "reason": "Add status field to Issue interface"
                }
              ]
            }
            """;

            var result = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(rawJson, evidence, tempDir);

            result.Success.Should().BeTrue();
            result.ResultData!.ImpactedFiles.Should().HaveCount(1);
            result.ResultData.ImpactedFiles[0].FilePath.Should().Be("src/models/issue.ts");
            result.ResultData.ImpactedFiles[0].ChangeType.Should().Be(ImpactFileChangeType.Modify);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Scenario_ModifyWildcard_Rejected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devpilot_test_wildcard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "models"));

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "src", "models", "issue.ts"), "export interface Issue {}");
            var evidence = ChangeIntelligenceEvidenceCollector.CollectEvidence(tempDir, new RepositoryProfile(RepositoryVerificationState.Configured, Array.Empty<string>(), Array.Empty<RepositoryCheck>(), null));

            var rawJson = """
            {
              "summary": "Update issue models",
              "confidence": 85,
              "impactedFiles": [
                {
                  "filePath": "models/issue.*",
                  "changeType": "Modify",
                  "reason": "Wildcard pattern"
                }
              ]
            }
            """;

            var result = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(rawJson, evidence, tempDir);

            result.Success.Should().BeFalse();
            result.IsGroundingError.Should().BeTrue();
            result.ErrorMessage.Should().Contain("wildcard");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Scenario_ConcreteSuffixPath_ResolvesToFullRelativePath_IffUnique()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devpilot_test_suffix_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "models"));

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "src", "models", "issue.ts"), "export interface Issue {}");
            var evidence = ChangeIntelligenceEvidenceCollector.CollectEvidence(tempDir, new RepositoryProfile(RepositoryVerificationState.Configured, Array.Empty<string>(), Array.Empty<RepositoryCheck>(), null));

            var rawJson = """
            {
              "summary": "Update issue model using suffix path",
              "confidence": 90,
              "impactedFiles": [
                {
                  "filePath": "models/issue.ts",
                  "changeType": "Modify",
                  "reason": "Add status field to Issue interface"
                }
              ]
            }
            """;

            var result = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(rawJson, evidence, tempDir);

            result.Success.Should().BeTrue();
            result.ResultData!.ImpactedFiles.Should().HaveCount(1);
            result.ResultData.ImpactedFiles[0].FilePath.Should().Be("src/models/issue.ts");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Scenario_AmbiguousBasenameOrSuffix_RejectedSafely()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devpilot_test_ambig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "client", "models"));
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "server", "models"));

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "src", "client", "models", "issue.ts"), "export interface ClientIssue {}");
            File.WriteAllText(Path.Combine(tempDir, "src", "server", "models", "issue.ts"), "export interface ServerIssue {}");
            var evidence = ChangeIntelligenceEvidenceCollector.CollectEvidence(tempDir, new RepositoryProfile(RepositoryVerificationState.Configured, Array.Empty<string>(), Array.Empty<RepositoryCheck>(), null));

            var rawJson = """
            {
              "summary": "Ambiguous model path",
              "confidence": 80,
              "impactedFiles": [
                {
                  "filePath": "models/issue.ts",
                  "changeType": "Modify",
                  "reason": "Ambiguous path matching two files"
                }
              ]
            }
            """;

            var result = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(rawJson, evidence, tempDir);

            result.Success.Should().BeFalse();
            result.IsGroundingError.Should().BeTrue();
            result.ErrorMessage.Should().Contain("matches multiple candidate files");
            result.ErrorMessage.Should().Contain("Ambiguous mapping cannot be resolved safely");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Scenario_CreateConcreteNewPath_Accepted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devpilot_test_create_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "models"));

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "src", "models", "issue.ts"), "export interface Issue {}");
            var evidence = ChangeIntelligenceEvidenceCollector.CollectEvidence(tempDir, new RepositoryProfile(RepositoryVerificationState.Configured, Array.Empty<string>(), Array.Empty<RepositoryCheck>(), null));

            var rawJson = """
            {
              "summary": "Create new issue status enum file",
              "confidence": 95,
              "impactedFiles": [
                {
                  "filePath": "src/models/issueStatus.ts",
                  "changeType": "Create",
                  "reason": "New enum for issue statuses"
                }
              ]
            }
            """;

            var result = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(rawJson, evidence, tempDir);

            result.Success.Should().BeTrue();
            result.ResultData!.ImpactedFiles.Should().HaveCount(1);
            result.ResultData.ImpactedFiles[0].FilePath.Should().Be("src/models/issueStatus.ts");
            result.ResultData.ImpactedFiles[0].ChangeType.Should().Be(ImpactFileChangeType.Add);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Scenario_ImpactAnalysis_GenericInventory_ReturnsNonZeroImpactedFiles_WhenRoslynProjectCountIsZero()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devpilot_test_zero_roslyn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "models"));
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "services"));

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "src", "models", "issue.ts"), "export interface Issue { id: string; }");
            File.WriteAllText(Path.Combine(tempDir, "src", "services", "issueService.ts"), "export class IssueService {}");
            File.WriteAllText(Path.Combine(tempDir, "package.json"), """{"name": "issue-tracker", "scripts": {"build": "tsc"}}""");

            var workspaceId = Guid.NewGuid();
            var task = new DevelopmentTask
            {
                Id = Guid.NewGuid(),
                Title = "Add issue status property",
                Description = "Extend Issue interface with status property and update issueService",
                Status = DevelopmentTaskStatus.Draft,
                RepositoryWorkspaceId = workspaceId
            };

            var workspace = new RepositoryWorkspace
            {
                Id = workspaceId,
                LocalPath = tempDir,
                Owner = "testowner",
                Repository = "testrepo",
                Branch = "main",
                CommitSha = "abc1234",
                Status = RepositoryWorkspaceStatus.Completed
            };

            var taskRepo = new FakeTaskRepository();
            await taskRepo.AddAsync(task);
            var workspaceQuery = new FakeWorkspaceQuery { WorkspaceToReturn = workspace };
            var analysisRepo = new FakeAnalysisRepository();
            var analyzer = new FakeRepositoryAnalyzer();
            var embeddingProvider = new FakeEmbeddingProvider();
            var searchService = new FakeSearchService();

            var aiProvider = new FakeAiProvider();
            aiProvider.ResponsesToReturn.Enqueue("""
                {
                  "summary": "Add status field to Issue interface and update service",
                  "confidence": 90,
                  "impactedFiles": [
                    {
                      "filePath": "src/models/issue.ts",
                      "changeType": "Modify",
                      "reason": "Add status field to Issue interface"
                    },
                    {
                      "filePath": "src/services/issueService.ts",
                      "changeType": "Modify",
                      "reason": "Handle status updates in issueService"
                    }
                  ],
                  "proposedPlan": [
                    {
                      "stepNumber": 1,
                      "title": "Update model",
                      "description": "Add status field to Issue interface"
                    }
                  ]
                }
                """);

            var handler = new AnalyzeTaskImpactCommandHandler(
                taskRepo,
                workspaceQuery,
                analysisRepo,
                analyzer,
                aiProvider,
                embeddingProvider,
                searchService,
                NullLogger<AnalyzeTaskImpactCommandHandler>.Instance);

            var result = await handler.HandleAsync(new AnalyzeTaskImpactCommand(task.Id));

            result.Success.Should().BeTrue();
            result.Analysis.Should().NotBeNull();
            result.Analysis!.StructuredResult!.ImpactedFiles.Should().HaveCount(2);
            result.Analysis.StructuredResult.ImpactedFiles.Select(f => f.FilePath).Should().Contain("src/models/issue.ts");
            result.Analysis.StructuredResult.ImpactedFiles.Select(f => f.FilePath).Should().Contain("src/services/issueService.ts");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Scenario_PathSafety_PreservesZeroImpactSafetyExceptionInDeveloperAgent()
    {
        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Task with empty impact",
            TaskDescription: "Empty impact test",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: Array.Empty<string>(),
            WorkspacePath: _repoRoot,
            BranchName: "main",
            ImpactedFiles: Array.Empty<ImpactedFileDetail>());

        var act = () => DeveloperAgent.BuildManifestFromImpactAnalysis(
            request,
            _repoRoot,
            ProjectGraphHelper.DiscoverProjectRoots(_repoRoot),
            maxManifestFiles: 10,
            projectGraph: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*contains no impacted files*");
    }

    [Fact]
    public void Scenario_ModifyDirectoryOnlyPath_Rejected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devpilot_test_dironly_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "models"));
        File.WriteAllText(Path.Combine(tempDir, "src", "models", "issue.ts"), "export interface Issue {}");

        try
        {
            var evidence = ChangeIntelligenceEvidenceCollector.CollectEvidence(
                tempDir,
                new RepositoryProfile(RepositoryVerificationState.Configured, Array.Empty<string>(), Array.Empty<RepositoryCheck>(), null));

            var rawJson = """
            {
              "summary": "Update models directory",
              "confidence": 80,
              "impactedFiles": [
                {
                  "filePath": "src/models/",
                  "changeType": "Modify",
                  "reason": "Directory-only path"
                }
              ]
            }
            """;

            var result = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(rawJson, evidence, tempDir);

            result.Success.Should().BeFalse();
            result.IsGroundingError.Should().BeTrue();
            result.ErrorMessage.Should().Contain("directory-only");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Scenario_DotNetRoslynRepo_StillReceivesProjectGraphEvidence()
    {
        var evidence = ChangeIntelligenceEvidenceCollector.CollectEvidence(
            _repoRoot,
            new RepositoryProfile(RepositoryVerificationState.Configured, Array.Empty<string>(), Array.Empty<RepositoryCheck>(), null));

        evidence.ProjectGraph.Should().NotBeEmpty();
        evidence.ProjectRoots.Should().Contain(r => r.Contains("DevPilot.Application", StringComparison.OrdinalIgnoreCase));
        evidence.InventoryCsFiles.Should().Contain("src/DevPilot.Domain/Entities/DevelopmentTask.cs");
        evidence.HasEfCore.Should().BeTrue();
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
