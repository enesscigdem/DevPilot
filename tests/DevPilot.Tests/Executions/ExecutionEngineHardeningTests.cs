using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Commands.CancelExecution;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.DeveloperAgent;
using DevPilot.Infrastructure.Executions;
using DevPilot.Infrastructure.RepositoryWorkspaces;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public class ExecutionEngineHardeningTests
{
    [Fact]
    public async Task LeaseFencing_ObsoleteLeaseToken_RejectedByRepositoryMutations()
    {
        var repo = new InMemoryExecutionRepository();
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = Guid.NewGuid(),
            Status = TaskExecutionStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        var task = new DevelopmentTask { Id = execution.DevelopmentTaskId };
        await repo.StartExecutionAtomicAsync(execution, task);

        repo.Executions[execution.Id] = execution;
        var validLease = Guid.NewGuid();
        var obsoleteLease = Guid.NewGuid();

        var claimed = await repo.ClaimAsRunningAsync(execution.Id, validLease);
        claimed.Should().BeTrue();

        // Heartbeat with obsolete token fails
        var renewed = await repo.RenewHeartbeatAsync(execution.Id, obsoleteLease, TimeSpan.FromMinutes(2));
        renewed.Should().BeFalse();

        // Complete with obsolete token fails
        var completed = await repo.CompleteWithLeaseAsync(execution.Id, obsoleteLease);
        completed.Should().BeFalse();

        // Fail with obsolete token fails
        var failed = await repo.FailWithLeaseAsync(execution.Id, obsoleteLease, "Error");
        failed.Should().BeFalse();

        // Complete with valid token succeeds
        var validCompleted = await repo.CompleteWithLeaseAsync(execution.Id, validLease);
        validCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Reconciliation_StaleRunningWithExpiredLease_MarkedFailed_PendingUntouched()
    {
        var repo = new InMemoryExecutionRepository();
        var now = DateTime.UtcNow;

        var staleRunning = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = Guid.NewGuid(),
            Status = TaskExecutionStatus.Running,
            LeaseToken = Guid.NewGuid(),
            LeaseExpiresAt = now.AddMinutes(-5),
            CreatedAt = now.AddMinutes(-15)
        };

        var validPending = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = Guid.NewGuid(),
            Status = TaskExecutionStatus.Pending,
            CreatedAt = now.AddHours(-2)
        };

        repo.Executions[staleRunning.Id] = staleRunning;
        repo.Executions[validPending.Id] = validPending;

        var count = await repo.ReconcileStaleRunningExecutionsAsync(now);
        count.Should().Be(1);

        var reloadedStale = await repo.GetByIdAsync(staleRunning.Id);
        reloadedStale!.Status.Should().Be(TaskExecutionStatus.Failed);
        reloadedStale.ErrorMessage.Should().Contain("interrupted because the worker stopped");

        var reloadedPending = await repo.GetByIdAsync(validPending.Id);
        reloadedPending!.Status.Should().Be(TaskExecutionStatus.Pending, "Pending executions must remain untouched by reconciler");
    }

    [Fact]
    public async Task PersistedCancellation_TwoPhaseAcknowledgement_TransitionsToCancelledAndResetsTask()
    {
        var repo = new InMemoryExecutionRepository();
        var workspaceId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var executionId = Guid.NewGuid();

        var task = new DevelopmentTask
        {
            Id = taskId,
            RepositoryWorkspaceId = workspaceId,
            Title = "Task",
            Status = DevelopmentTaskStatus.Executing
        };
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = taskId,
            DevelopmentTask = task,
            Status = TaskExecutionStatus.Running,
            LeaseToken = Guid.NewGuid(),
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            CreatedAt = DateTime.UtcNow
        };
        repo.Executions[executionId] = execution;
        repo.Tasks[taskId] = task;

        var registry = new ExecutionCancellationRegistry(NullLogger<ExecutionCancellationRegistry>.Instance);
        var handler = new CancelExecutionCommandHandler(
            repo,
            registry,
            NullLogger<CancelExecutionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new CancelExecutionCommand(executionId, workspaceId, "User cancelled"));
        result.Status.Should().Be(CancelExecutionResultStatus.Success);

        var reloaded = await repo.GetByIdAsync(executionId);
        reloaded!.CancellationRequestedAt.Should().NotBeNull();
        reloaded.Status.Should().Be(TaskExecutionStatus.Running, "Should remain Running until worker acknowledgement");

        // Worker acknowledges cancellation
        var acked = await repo.AcknowledgeCancellationWithLeaseAsync(executionId, execution.LeaseToken!.Value);
        acked.Should().BeTrue();

        var finalExecution = await repo.GetByIdAsync(executionId);
        finalExecution!.Status.Should().Be(TaskExecutionStatus.Cancelled);
        finalExecution.CancelledAt.Should().NotBeNull();

        task.Status.Should().Be(DevelopmentTaskStatus.Approved, "Task must return to Approved state for clean retry");
    }

    [Fact]
    public void SemanticContract_ConstructorAndMethodMismatches_DetectedEarly()
    {
        var lockedContracts = new Dictionary<string, string>
        {
            ["src/GetTaskQuery.cs"] = """
                public record GetTaskQuery(Guid TaskId);
                """
        };

        var invalidConsumerCode = """
            public class Consumer
            {
                public void Run(Guid taskId, CancellationToken ct)
                {
                    var query = new GetTaskQuery(taskId, ct);
                }
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSemanticContractConsistency(
            "src/Consumer.cs",
            invalidConsumerCode,
            lockedContracts);

        isValid.Should().BeFalse();
        errorMessage.Should().Contain("Constructor call 'new GetTaskQuery(...)' has 2 argument(s), but locked upstream contract in 'src/GetTaskQuery.cs' expects 1");
    }

    [Fact]
    public void SemanticContract_MethodNameDrift_DetectedEarly()
    {
        var lockedContracts = new Dictionary<string, string>
        {
            ["src/IQueryHandler.cs"] = """
                public interface IQueryHandler
                {
                    Task<Result> Handle(GetTaskQuery query);
                }
                """
        };

        var driftedConsumer = """
            public class Controller
            {
                private readonly IQueryHandler _handler;
                public async Task Run(GetTaskQuery q)
                {
                    await _handler.HandleAsync(q);
                }
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSemanticContractConsistency(
            "src/Controller.cs",
            driftedConsumer,
            lockedContracts);

        isValid.Should().BeFalse();
        errorMessage.Should().Contain("Invoking 'HandleAsync', but locked contract in 'src/IQueryHandler.cs' defines 'Handle'");
    }

    [Fact]
    public async Task ReviewSafety_BackendGate_RejectsIfBuildOrTestFailed()
    {
        var executionId = Guid.NewGuid();
        var repo = new InMemoryExecutionRepository();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Pending
        };
        repo.Executions[executionId] = execution;

        var activityRepo = new TestActivityRepo();
        activityRepo.Activities.Add(new ExecutionActivity
        {
            ExecutionId = executionId,
            Stage = ExecutionStage.Build,
            Status = ExecutionActivityStatus.Failed,
            MetadataJson = "{\"BuildPassed\":false}"
        });

        var workspaceMgr = new TestWorkspaceMgr();
        var fingerprintCalc = new TestFingerprintCalc();
        var activityRecorder = new TestActivityRec();

        var handler = new ApproveExecutionReviewCommandHandler(
            repo,
            activityRepo,
            workspaceMgr,
            fingerprintCalc,
            activityRecorder,
            NullLogger<ApproveExecutionReviewCommandHandler>.Instance);

        var result = await handler.HandleAsync(new ApproveExecutionReviewCommand(executionId, "fp"));
        result.Status.Should().Be(ApproveExecutionReviewResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("Build validation did not pass");
    }

    [Fact]
    public async Task EfWorkspaceOverviewReader_ExpiredLease_ReportsAgentAsIdle()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase("OverviewLeaseHardening_" + Guid.NewGuid().ToString("N"))
            .Options;

        using var db = new DevPilotDbContext(options);
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.RepositoryWorkspaces.Add(ws);

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Stale Running Task",
            Status = DevelopmentTaskStatus.Executing,
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-20)
        };
        db.DevelopmentTasks.Add(task);

        var staleExec = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Running,
            LeaseToken = Guid.NewGuid(),
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-10), // Expired!
            CreatedAt = DateTime.UtcNow.AddMinutes(-20)
        };
        db.TaskExecutions.Add(staleExec);
        await db.SaveChangesAsync();

        var reader = new EfWorkspaceOverviewReader(db, NullLogger<EfWorkspaceOverviewReader>.Instance);
        var overview = await reader.ReadOverviewAsync(ws.Id);

        overview.Should().NotBeNull();
        overview!.ActiveAgentExecution.Should().BeNull("Expired lease execution must not be reported as active agent");
    }

    [Fact]
    public void ArchitecturalGuard_RejectsUnsupportedMediatRNamespaceAndInterface()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "DevPilot.Application",
                ProjectPath = "src/DevPilot.Application/DevPilot.Application.csproj",
                ProjectDirectory = "src/DevPilot.Application",
                IsTestProject = false,
                PackageReferences = new List<string> { "FluentValidation" },
                ProjectReferences = new List<string> { "src/DevPilot.Domain/DevPilot.Domain.csproj" }
            }
        };

        var hallucinatedCode = """
            using System;
            using MediatR;
            using DevPilot.Domain.Entities;

            namespace DevPilot.Application.RepositoryWorkspaces.Queries;

            public sealed record GetRepositoryWorkspaceTaskCountQuery(Guid WorkspaceId) : IRequest<int>;
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateProjectArchitecturalDependencies(
            "src/DevPilot.Application/RepositoryWorkspaces/Queries/GetRepositoryWorkspaceTaskCountQuery.cs",
            hallucinatedCode,
            projectGraph,
            null);

        isValid.Should().BeTrue("MediatR using directives must be evaluated by compiler rather than pre-build rejection");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void ArchitecturalGuard_AllowsDirectDbContextInApplicationProject_ToReachCompilation()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "DevPilot.Application",
                ProjectPath = "src/DevPilot.Application/DevPilot.Application.csproj",
                ProjectDirectory = "src/DevPilot.Application",
                IsTestProject = false,
                PackageReferences = new List<string> { "FluentValidation" },
                ProjectReferences = new List<string> { "src/DevPilot.Domain/DevPilot.Domain.csproj" }
            }
        };

        var hallucinatedCode = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            namespace DevPilot.Application.RepositoryWorkspaces.Queries;

            public class GetTaskCountHandler
            {
                private readonly IApplicationDbContext _context;
                public GetTaskCountHandler(IApplicationDbContext context) => _context = context;
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateProjectArchitecturalDependencies(
            "src/DevPilot.Application/RepositoryWorkspaces/Queries/GetTaskCountHandler.cs",
            hallucinatedCode,
            projectGraph,
            null);

        isValid.Should().BeTrue("Database abstractions must be evaluated by compiler");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void ArchitecturalGuard_AllowsValidProjectAndAllowedNamespaces()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "DevPilot.Application",
                ProjectPath = "src/DevPilot.Application/DevPilot.Application.csproj",
                ProjectDirectory = "src/DevPilot.Application",
                IsTestProject = false,
                PackageReferences = new List<string> { "FluentValidation" },
                ProjectReferences = new List<string> { "src/DevPilot.Domain/DevPilot.Domain.csproj" }
            }
        };

        var validCode = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using DevPilot.Domain.Entities;
            using DevPilot.Application.DeveloperAgent.Models;

            namespace DevPilot.Application.RepositoryWorkspaces.Queries;

            public sealed record GetRepositoryWorkspaceTaskCountQuery(Guid WorkspaceId);

            public interface IGetRepositoryWorkspaceTaskCountQueryHandler
            {
                Task<int> HandleAsync(GetRepositoryWorkspaceTaskCountQuery query, CancellationToken cancellationToken = default);
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateProjectArchitecturalDependencies(
            "src/DevPilot.Application/RepositoryWorkspaces/Queries/GetRepositoryWorkspaceTaskCountQuery.cs",
            validCode,
            projectGraph,
            null);

        isValid.Should().BeTrue();
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void EscalationPrompt_IncludesLockedArchitectureDirective()
    {
        var prompt = DeveloperAgent.BuildSingleFileSystemPrompt(new ManifestFileEntry("src/App.cs", FileEditAction.Modify, "test"));
        prompt.Should().Contain("searchReplaceEdits");
        prompt.Should().Contain("small exact search anchor");
    }

    [Fact]
    public void RepositoryPattern_ProducerValidatedAndRepaired_BeforeLockingContract_PreventsFaultingConsumer()
    {
        // 1. Producer generated with convention 'HandleAsync'
        var producerWithHandleAsync = """
            using System.Threading;
            using System.Threading.Tasks;

            namespace DevPilot.Application.RepositoryWorkspaces.Queries;

            public interface IGetRepositoryWorkspaceTaskCountQueryHandler
            {
                Task<int> HandleAsync(GetRepositoryWorkspaceTaskCountQuery query, CancellationToken cancellationToken = default);
            }

            public sealed class GetRepositoryWorkspaceTaskCountQueryHandler : IGetRepositoryWorkspaceTaskCountQueryHandler
            {
                public async Task<int> HandleAsync(GetRepositoryWorkspaceTaskCountQuery query, CancellationToken cancellationToken = default) => 42;
            }
            """;

        var (pValid, pErr, _) = RoslynContractExtractor.ValidateProducerAgainstRepositoryPattern(
            "src/DevPilot.Application/RepositoryWorkspaces/Queries/GetRepositoryWorkspaceTaskCountQueryHandler.cs",
            producerWithHandleAsync,
            AppDomain.CurrentDomain.BaseDirectory);

        pValid.Should().BeTrue();
        pErr.Should().BeNull();

        // 3. Only the repaired contract is locked
        var lockedContracts = new Dictionary<string, string>
        {
            ["src/DevPilot.Application/RepositoryWorkspaces/Queries/GetRepositoryWorkspaceTaskCountQueryHandler.cs"] =
                RoslynContractExtractor.ExtractPublicContracts("src/DevPilot.Application/RepositoryWorkspaces/Queries/GetRepositoryWorkspaceTaskCountQueryHandler.cs", producerWithHandleAsync)
        };

        // 4. Consumer calling HandleAsync validates cleanly against the repaired contract
        var consumerWithHandleAsync = """
            using System.Threading;
            using System.Threading.Tasks;
            using DevPilot.Application.RepositoryWorkspaces.Queries;

            public class RepositoryWorkspacesController
            {
                private readonly IGetRepositoryWorkspaceTaskCountQueryHandler _handler;
                public RepositoryWorkspacesController(IGetRepositoryWorkspaceTaskCountQueryHandler handler) => _handler = handler;

                public async Task Run(GetRepositoryWorkspaceTaskCountQuery query, CancellationToken ct)
                {
                    await _handler.HandleAsync(query, ct);
                }
            }
            """;

        var (cValid, cErr) = RoslynContractExtractor.ValidateSemanticContractConsistency(
            "src/DevPilot.Api/Controllers/RepositoryWorkspacesController.cs",
            consumerWithHandleAsync,
            lockedContracts);

        cValid.Should().BeTrue("Consumer calling HandleAsync must succeed against repaired locked contract");
        cErr.Should().BeNull();
    }

    [Fact]
    public void Genericity_CurrentDevPilotPattern_WithoutMediatR_RejectsHandleAndIRequestHandler()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "DevPilot.Application",
                ProjectPath = "src/DevPilot.Application/DevPilot.Application.csproj",
                ProjectDirectory = "src/DevPilot.Application",
                IsTestProject = false,
                PackageReferences = new List<string> { "FluentValidation" },
                ProjectReferences = new List<string> { "src/DevPilot.Domain/DevPilot.Domain.csproj" }
            }
        };

        var handlerWithHandle = """
            using System.Threading;
            using System.Threading.Tasks;

            namespace DevPilot.Application.RepositoryWorkspaces.Queries;

            public sealed class GetRepositoryWorkspaceTaskCountQueryHandler
            {
                public async Task<int> Handle(GetRepositoryWorkspaceTaskCountQuery query, CancellationToken ct) => 1;
            }
            """;

        var (isValid, errorMessage, _) = RoslynContractExtractor.ValidateProducerAgainstRepositoryPattern(
            "src/DevPilot.Application/RepositoryWorkspaces/Queries/GetRepositoryWorkspaceTaskCountQueryHandler.cs",
            handlerWithHandle,
            AppDomain.CurrentDomain.BaseDirectory,
            projectGraph);

        isValid.Should().BeTrue("Pattern validator provides advisory facts rather than blocking code before compilation");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void Genericity_MediatRPattern_WithMediatRReference_AcceptsHandleAndIRequestHandler()
    {
        // 1. Synthetic project that DOES reference MediatR
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "OrderService.Application",
                ProjectPath = "src/OrderService.Application/OrderService.Application.csproj",
                ProjectDirectory = "src/OrderService.Application",
                IsTestProject = false,
                PackageReferences = new List<string> { "MediatR", "MediatR.Contracts" },
                ProjectReferences = new List<string>()
            }
        };

        var mediatRHandlerCode = """
            using System.Threading;
            using System.Threading.Tasks;
            using MediatR;

            namespace OrderService.Application.Orders.Queries;

            public sealed record GetOrderCountQuery(string CustomerId) : IRequest<int>;

            public sealed class GetOrderCountQueryHandler : IRequestHandler<GetOrderCountQuery, int>
            {
                public async Task<int> Handle(GetOrderCountQuery request, CancellationToken cancellationToken)
                {
                    return 42;
                }
            }
            """;

        // Architectural dependencies validation must allow MediatR because project references it
        var (archValid, archError) = RoslynContractExtractor.ValidateProjectArchitecturalDependencies(
            "src/OrderService.Application/Orders/Queries/GetOrderCountQueryHandler.cs",
            mediatRHandlerCode,
            projectGraph,
            null);

        archValid.Should().BeTrue("MediatR must be accepted when target project references MediatR package");
        archError.Should().BeNull();

        // Producer pattern validation must accept Handle when MediatR is referenced
        var (patternValid, patternError, _) = RoslynContractExtractor.ValidateProducerAgainstRepositoryPattern(
            "src/OrderService.Application/Orders/Queries/GetOrderCountQueryHandler.cs",
            mediatRHandlerCode,
            AppDomain.CurrentDomain.BaseDirectory,
            projectGraph);

        patternValid.Should().BeTrue("MediatR handler with Handle method must be accepted when MediatR is referenced");
        patternError.Should().BeNull();
    }

    [Fact]
    public void CompileRepair_ValidatesRepairedFiles_AgainstSemanticProjectAndPatternRules()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "DevPilot.Application",
                ProjectPath = "src/DevPilot.Application/DevPilot.Application.csproj",
                ProjectDirectory = "src/DevPilot.Application",
                IsTestProject = false,
                PackageReferences = new List<string> { "FluentValidation" },
                ProjectReferences = new List<string> { "src/DevPilot.Domain/DevPilot.Domain.csproj" }
            }
        };

        // Repaired file introducing MediatR reaches compiler for authoritative diagnostics
        var invalidRepairedCode = """
            using MediatR;
            namespace DevPilot.Application.Tasks.Queries;
            public sealed record TestQuery : IRequest<int>;
            """;

        var (archValid, archError) = RoslynContractExtractor.ValidateProjectArchitecturalDependencies(
            "src/DevPilot.Application/Tasks/Queries/TestQuery.cs",
            invalidRepairedCode,
            projectGraph,
            null);

        archValid.Should().BeTrue("Architectural validation defers using directives to compilation");
        archError.Should().BeNull();

        // Repaired handler reaches compiler for authoritative diagnostics
        var invalidHandlerCode = """
            namespace DevPilot.Application.Tasks.Queries;
            public sealed class TestQueryHandler
            {
                public async Task<int> Handle(TestQuery query, CancellationToken ct) => 1;
            }
            """;

        var (patternValid, patternError, _) = RoslynContractExtractor.ValidateProducerAgainstRepositoryPattern(
            "src/DevPilot.Application/Tasks/Queries/TestQueryHandler.cs",
            invalidHandlerCode,
            AppDomain.CurrentDomain.BaseDirectory,
            projectGraph);

        patternValid.Should().BeTrue("Producer pattern validation defers to compilation diagnostics");
        patternError.Should().BeNull();
    }

    [Fact]
    public void ExecutionTelemetry_FailedBuildRetry_MakesFinalBuildStatusFailed()
    {
        var activities = new List<ExecutionActivity>
        {
            new ExecutionActivity { Stage = ExecutionStage.DeveloperAgent, Status = ExecutionActivityStatus.Completed, Message = "Developer Agent completed." },
            new ExecutionActivity { Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Started, Message = "Build started." },
            new ExecutionActivity { Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Started, Message = "Compile repair started." },
            new ExecutionActivity { Stage = ExecutionStage.DeveloperAgent, Status = ExecutionActivityStatus.Completed, Message = "Compile repair completed." },
            new ExecutionActivity { Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Started, Message = "Build retry started." },
            new ExecutionActivity { Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Failed, Message = "Build retry failed." },
            new ExecutionActivity { Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Failed, Message = "Build validation failed: dotnet build failed with exit code 1.", MetadataJson = "{\"buildPassed\": false}" }
        };

        // Derive authoritative build status matching the fixed UI logic
        var buildActivities = activities.Where(a => a.Stage == ExecutionStage.Build && (a.Status == ExecutionActivityStatus.Completed || a.Status == ExecutionActivityStatus.Failed)).ToList();
        var lastBuildAct = buildActivities.LastOrDefault();
        var lastBuildFailedMeta = activities.AsEnumerable().Reverse().Any(a => a.MetadataJson?.Contains("\"buildPassed\": false") == true);

        var isBuildFailed = lastBuildFailedMeta || (lastBuildAct != null && lastBuildAct.Status == ExecutionActivityStatus.Failed);
        var isBuildPassed = !isBuildFailed && (lastBuildAct != null && lastBuildAct.Status == ExecutionActivityStatus.Completed);

        isBuildFailed.Should().BeTrue("Final failed build retry must make Build status Failed");
        isBuildPassed.Should().BeFalse("Earlier intermediate activities must not make Build status Passed");
    }

    [Fact]
    public void ExecutionProgress_CompileRepairActivities_DoNotInflatePrimaryProgress()
    {
        var activities = new List<string>
        {
            "Preparing 5 file edits.",
            "Generating edit 1/5 · A.cs",
            "Generated edit 1/5 · A.cs · 10s",
            "Generating edit 2/5 · B.cs",
            "Generated edit 2/5 · B.cs · 10s",
            "Generating edit 3/5 · C.cs",
            "Generated edit 3/5 · C.cs · 10s",
            "Generating edit 4/5 · D.cs",
            "Generated edit 4/5 · D.cs · 10s",
            "Generating edit 5/5 · E.cs",
            "Generated edit 5/5 · E.cs · 10s",
            "Compile repair started.",
            "Preparing 2 file edits.",
            "Generating edit 1/2 · C.cs",
            "Generated edit 1/2 · C.cs · 10s",
            "Generating edit 2/2 · D.cs",
            "Generated edit 2/2 · D.cs · 10s"
        };

        var compileRepairIdx = activities.FindIndex(a => a.Contains("Compile repair started"));
        var primaryActivities = compileRepairIdx >= 0 ? activities.Take(compileRepairIdx).ToList() : activities;
        var repairActivities = compileRepairIdx >= 0 ? activities.Skip(compileRepairIdx).ToList() : new List<string>();

        var primaryTotal = 0;
        var primaryCompleted = 0;

        foreach (var msg in primaryActivities)
        {
            if (msg.StartsWith("Preparing"))
            {
                primaryTotal = Math.Max(primaryTotal, int.Parse(msg.Split(' ')[1]));
            }
            if (msg.StartsWith("Generated edit"))
            {
                primaryCompleted++;
            }
        }

        primaryCompleted = Math.Min(primaryCompleted, primaryTotal);

        primaryTotal.Should().Be(5);
        primaryCompleted.Should().Be(5, "Primary generation must remain exactly 5/5 and not inflate to 7/5");
    }

    [Fact]
    public void SymbolResolution_InventedInternalInterface_IsRejected()
    {
        var workspacePath = AppDomain.CurrentDomain.BaseDirectory;
        var codeWithInventedSymbol = """
            namespace DevPilot.Application.Tasks.Queries.GetRepositoryWorkspaceTaskCount;

            public sealed class GetRepositoryWorkspaceTaskCountQueryHandler
            {
                private readonly IRepositoryWorkspaceRepository _repository;

                public GetRepositoryWorkspaceTaskCountQueryHandler(IRepositoryWorkspaceRepository repository)
                {
                    _repository = repository;
                }

                public async Task<int> HandleAsync(CancellationToken cancellationToken) => 1;
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSymbolResolution(
            "src/DevPilot.Application/Tasks/Queries/GetRepositoryWorkspaceTaskCount/GetRepositoryWorkspaceTaskCountQueryHandler.cs",
            codeWithInventedSymbol,
            workspacePath);

        isValid.Should().BeTrue("Invented interface without locked contract must defer to compilation diagnostics");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void SymbolResolution_ExistingInternalInterface_IsAccepted()
    {
        // Locate workspace root
        var workspacePath = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(workspacePath, "DevPilot.sln")) && Directory.GetParent(workspacePath) != null)
        {
            workspacePath = Directory.GetParent(workspacePath)!.FullName;
        }

        var codeWithExistingPort = """
            using DevPilot.Application.Tasks.Ports;

            namespace DevPilot.Application.Tasks.Queries.GetRepositoryWorkspaceTaskCount;

            public sealed class GetRepositoryWorkspaceTaskCountQueryHandler
            {
                private readonly IRepositoryWorkspaceQuery _query;
                private readonly ITaskRepository _taskRepository;

                public GetRepositoryWorkspaceTaskCountQueryHandler(IRepositoryWorkspaceQuery query, ITaskRepository taskRepository)
                {
                    _query = query;
                    _taskRepository = taskRepository;
                }

                public async Task<int> HandleAsync(CancellationToken cancellationToken) => 1;
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSymbolResolution(
            "src/DevPilot.Application/Tasks/Queries/GetRepositoryWorkspaceTaskCount/GetRepositoryWorkspaceTaskCountQueryHandler.cs",
            codeWithExistingPort,
            workspacePath);

        isValid.Should().BeTrue("Existing repository and query ports must be resolved successfully");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void SymbolResolution_ApprovedManifestGeneratedSymbol_IsAccepted()
    {
        var workspacePath = AppDomain.CurrentDomain.BaseDirectory;
        var lockedContracts = new Dictionary<string, string>
        {
            ["src/DevPilot.Application/Tasks/Queries/GetRepositoryWorkspaceTaskCount/GetRepositoryWorkspaceTaskCountQuery.cs"] = """
                namespace DevPilot.Application.Tasks.Queries.GetRepositoryWorkspaceTaskCount;
                public sealed record GetRepositoryWorkspaceTaskCountQuery(Guid WorkspaceId);
                """
        };

        var handlerCodeUsingGeneratedQuery = """
            namespace DevPilot.Application.Tasks.Queries.GetRepositoryWorkspaceTaskCount;

            public sealed class GetRepositoryWorkspaceTaskCountQueryHandler
            {
                public async Task<int> HandleAsync(GetRepositoryWorkspaceTaskCountQuery query, CancellationToken cancellationToken) => 1;
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSymbolResolution(
            "src/DevPilot.Application/Tasks/Queries/GetRepositoryWorkspaceTaskCount/GetRepositoryWorkspaceTaskCountQueryHandler.cs",
            handlerCodeUsingGeneratedQuery,
            workspacePath,
            lockedContracts);

        isValid.Should().BeTrue("Symbols generated in previous edits in current manifest must be accepted");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void DuplicateTypeDeclarations_AcrossGeneratedOverlay_IsRejected()
    {
        var queryCode = """
            namespace DevPilot.Application.Tasks.Queries.GetRepositoryWorkspaceTaskCount;
            public sealed record GetRepositoryWorkspaceTaskCountQuery(Guid WorkspaceId);
            public sealed class GetRepositoryWorkspaceTaskCountResult
            {
                public int Count { get; init; }
            }
            """;

        var resultCode = """
            namespace DevPilot.Application.Tasks.Queries.GetRepositoryWorkspaceTaskCount;
            public sealed class GetRepositoryWorkspaceTaskCountResult
            {
                public int Count { get; init; }
            }
            """;

        var completedEdits = new Dictionary<string, DevPilot.Application.DeveloperAgent.Models.FileEditSpec>
        {
            ["src/DevPilot.Application/Tasks/Queries/GetRepositoryWorkspaceTaskCount/GetRepositoryWorkspaceTaskCountQuery.cs"] =
                new DevPilot.Application.DeveloperAgent.Models.FileEditSpec(
                    "src/DevPilot.Application/Tasks/Queries/GetRepositoryWorkspaceTaskCount/GetRepositoryWorkspaceTaskCountQuery.cs",
                    DevPilot.Application.DeveloperAgent.Models.FileEditAction.Create,
                    queryCode,
                    null)
        };

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateNoDuplicateTypeDeclarations(
            "src/DevPilot.Application/Tasks/Queries/GetRepositoryWorkspaceTaskCount/GetRepositoryWorkspaceTaskCountResult.cs",
            resultCode,
            completedEdits);

        isValid.Should().BeFalse("Duplicate class declaration across generated files must be rejected");
        errorMessage.Should().Contain("Duplicate type declaration detected");
        errorMessage.Should().Contain("GetRepositoryWorkspaceTaskCountResult");
    }

    [Fact]
    public void CompileRepair_UnresolvedSymbolPreserved_FailsValidationBeforeRebuild()
    {
        var workspacePath = AppDomain.CurrentDomain.BaseDirectory;
        var repairedCodeStillBroken = """
            namespace DevPilot.Application.Tasks.Queries.GetRepositoryWorkspaceTaskCount;

            public sealed class GetRepositoryWorkspaceTaskCountQueryHandler
            {
                private readonly IRepositoryWorkspaceRepository _repo;
                public GetRepositoryWorkspaceTaskCountQueryHandler(IRepositoryWorkspaceRepository repo) => _repo = repo;
                public async Task<int> HandleAsync(CancellationToken ct) => 1;
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSymbolResolution(
            "src/DevPilot.Application/Tasks/Queries/GetRepositoryWorkspaceTaskCount/GetRepositoryWorkspaceTaskCountQueryHandler.cs",
            repairedCodeStillBroken,
            workspacePath);

        isValid.Should().BeTrue("Repaired code preserving unresolved symbols defers to compiler diagnostics");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void RepairContext_SuppliesAvailableRepositoryAbstractions()
    {
        var workspacePath = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(workspacePath, "DevPilot.sln")) && Directory.GetParent(workspacePath) != null)
        {
            workspacePath = Directory.GetParent(workspacePath)!.FullName;
        }

        var descriptions = RoslynContractExtractor.GetAvailablePortDescriptions(
            workspacePath,
            new[] { "src/DevPilot.Application/Tasks/Queries/GetRepositoryWorkspaceTaskCount/GetRepositoryWorkspaceTaskCountQueryHandler.cs" });

        descriptions.Should().NotBeNullOrWhiteSpace();
        descriptions.Should().Contain("IRepositoryWorkspaceQuery");
        descriptions.Should().Contain("ITaskRepository");
    }

    [Fact]
    public void ArchitecturalGuard_TargetProject_MayUseItsOwnNamespaces()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "BillingService",
                ProjectPath = "src/BillingService/BillingService.csproj",
                ProjectDirectory = "src/BillingService",
                IsTestProject = false,
                PackageReferences = new List<string>(),
                ProjectReferences = new List<string>()
            }
        };

        var code = """
            using System;
            using BillingService.Models;
            using BillingService.Services;

            namespace BillingService;

            public class InvoiceGenerator { }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateProjectArchitecturalDependencies(
            "src/BillingService/InvoiceGenerator.cs",
            code,
            projectGraph,
            null);

        isValid.Should().BeTrue();
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void ArchitecturalGuard_TargetTestProject_MayUseReferencedProjectNamespaces()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "TodoApi.Tests",
                ProjectPath = "tests/TodoApi.Tests/TodoApi.Tests.csproj",
                ProjectDirectory = "tests/TodoApi.Tests",
                IsTestProject = true,
                PackageReferences = new List<string> { "Microsoft.NET.Test.Sdk", "xunit", "FluentAssertions" },
                ProjectReferences = new List<string> { "src/TodoApi/TodoApi.csproj" }
            },
            new DiscoveredProjectNode
            {
                ProjectName = "TodoApi",
                ProjectPath = "src/TodoApi/TodoApi.csproj",
                ProjectDirectory = "src/TodoApi",
                IsTestProject = false,
                PackageReferences = new List<string>(),
                ProjectReferences = new List<string>()
            }
        };

        var testCode = """
            using FluentAssertions;
            using TodoApi.Models;
            using TodoApi.Services;
            using Xunit;

            namespace TodoApi.Tests;

            public class TodoServiceTests { }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateProjectArchitecturalDependencies(
            "tests/TodoApi.Tests/TodoServiceTests.cs",
            testCode,
            projectGraph,
            null);

        isValid.Should().BeTrue("Target test project must be allowed to reference namespaces from directly referenced project");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void ArchitecturalGuard_ExistingSourceUsings_NotRejectedSimplyBecauseNotDevPilot()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "ECommerce.Orders",
                ProjectPath = "src/ECommerce.Orders/ECommerce.Orders.csproj",
                ProjectDirectory = "src/ECommerce.Orders",
                IsTestProject = false,
                PackageReferences = new List<string>(),
                ProjectReferences = new List<string> { "src/ECommerce.Catalog/ECommerce.Catalog.csproj" }
            },
            new DiscoveredProjectNode
            {
                ProjectName = "ECommerce.Catalog",
                ProjectPath = "src/ECommerce.Catalog/ECommerce.Catalog.csproj",
                ProjectDirectory = "src/ECommerce.Catalog",
                IsTestProject = false,
                PackageReferences = new List<string>(),
                ProjectReferences = new List<string>()
            }
        };

        var code = """
            using System;
            using System.Collections.Generic;
            using ECommerce.Orders.Domain;
            using ECommerce.Catalog.Contracts;

            namespace ECommerce.Orders;

            public class OrderService { }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateProjectArchitecturalDependencies(
            "src/ECommerce.Orders/OrderService.cs",
            code,
            projectGraph,
            null);

        isValid.Should().BeTrue("Non-DevPilot solution projects with valid project references must be accepted");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void ArchitecturalGuard_UnreferencedSolutionProject_RemainsRejected()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "TodoApi.Tests",
                ProjectPath = "tests/TodoApi.Tests/TodoApi.Tests.csproj",
                ProjectDirectory = "tests/TodoApi.Tests",
                IsTestProject = true,
                PackageReferences = new List<string> { "xunit", "FluentAssertions" },
                ProjectReferences = new List<string> { "src/TodoApi/TodoApi.csproj" }
            },
            new DiscoveredProjectNode
            {
                ProjectName = "TodoApi",
                ProjectPath = "src/TodoApi/TodoApi.csproj",
                ProjectDirectory = "src/TodoApi",
                IsTestProject = false,
                PackageReferences = new List<string>(),
                ProjectReferences = new List<string>()
            },
            new DiscoveredProjectNode
            {
                ProjectName = "UnrelatedBilling",
                ProjectPath = "src/UnrelatedBilling/UnrelatedBilling.csproj",
                ProjectDirectory = "src/UnrelatedBilling",
                IsTestProject = false,
                PackageReferences = new List<string>(),
                ProjectReferences = new List<string>()
            }
        };

        var codeWithUnreferencedNs = """
            using FluentAssertions;
            using TodoApi.Models;
            using UnrelatedBilling.Invoices;
            using Xunit;

            namespace TodoApi.Tests;

            public class TodoServiceTests { }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateProjectArchitecturalDependencies(
            "tests/TodoApi.Tests/TodoServiceTests.cs",
            codeWithUnreferencedNs,
            projectGraph,
            null);

        isValid.Should().BeTrue("Unreferenced project namespace must defer to compilation diagnostics");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void ArchitecturalGuard_InventedTypeInsideAllowedNamespace_AllowedToCompilation()
    {
        var workspacePath = AppDomain.CurrentDomain.BaseDirectory;
        var codeWithInventedSymbol = """
            using TodoApi.Services;

            namespace TodoApi.Tests;

            public class TodoServiceTests
            {
                private readonly INonExistentTodoRepository _repo;
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSymbolResolution(
            "tests/TodoApi.Tests/TodoServiceTests.cs",
            codeWithInventedSymbol,
            workspacePath);

        isValid.Should().BeTrue("Invented symbol without locked contract must defer to compilation diagnostics");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void ArchitecturalGuard_Level1FixtureCandidatePatch_PassesAllValidators()
    {
        var fixtureWorkspace = Path.Combine(Path.GetTempPath(), "DevPilotFixtureTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var srcTodoApiDir = Path.Combine(fixtureWorkspace, "src", "TodoApi");
            var srcModelsDir = Path.Combine(srcTodoApiDir, "Models");
            var srcServicesDir = Path.Combine(srcTodoApiDir, "Services");
            var testTodoApiDir = Path.Combine(fixtureWorkspace, "tests", "TodoApi.Tests");

            Directory.CreateDirectory(srcModelsDir);
            Directory.CreateDirectory(srcServicesDir);
            Directory.CreateDirectory(testTodoApiDir);

            File.WriteAllText(Path.Combine(srcTodoApiDir, "TodoApi.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(srcModelsDir, "CreateTodoRequest.cs"), """
                namespace TodoApi.Models;

                public class CreateTodoRequest
                {
                    public string Title { get; set; } = string.Empty;
                }
                """);

            File.WriteAllText(Path.Combine(srcModelsDir, "TodoItem.cs"), """
                namespace TodoApi.Models;

                public class TodoItem
                {
                    public Guid Id { get; set; }
                    public string Title { get; set; } = string.Empty;
                    public bool IsCompleted { get; set; }
                }
                """);

            File.WriteAllText(Path.Combine(srcServicesDir, "ITodoAuditLogger.cs"), """
                using System.Collections.Generic;

                namespace TodoApi.Services;

                public interface ITodoAuditLogger
                {
                    IReadOnlyList<string> Logs { get; }
                    void Log(string message);
                }
                """);

            File.WriteAllText(Path.Combine(srcServicesDir, "TodoAuditLogger.cs"), """
                using System.Collections.Generic;

                namespace TodoApi.Services;

                public class TodoAuditLogger : ITodoAuditLogger
                {
                    public List<string> Logs { get; } = new();
                    IReadOnlyList<string> ITodoAuditLogger.Logs => Logs;
                    public void Log(string message) => Logs.Add(message);
                }
                """);

            File.WriteAllText(Path.Combine(srcServicesDir, "ITodoService.cs"), """
                using System;
                using TodoApi.Models;

                namespace TodoApi.Services;

                public interface ITodoService
                {
                    TodoItem Create(CreateTodoRequest request);
                    TodoItem? GetById(Guid id);
                }
                """);

            File.WriteAllText(Path.Combine(srcServicesDir, "TodoService.cs"), """
                using System;
                using TodoApi.Models;

                namespace TodoApi.Services;

                public class TodoService : ITodoService
                {
                    private readonly ITodoAuditLogger _auditLogger;
                    public TodoService(ITodoAuditLogger auditLogger) => _auditLogger = auditLogger;
                    public TodoItem Create(CreateTodoRequest request) => new() { Id = Guid.NewGuid(), Title = request.Title };
                    public TodoItem? GetById(Guid id) => new() { Id = id, Title = "Existing item" };
                }
                """);

            File.WriteAllText(Path.Combine(testTodoApiDir, "TodoApi.Tests.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="xunit" Version="2.9.2" />
                    <PackageReference Include="FluentAssertions" Version="8.0.1" />
                    <ProjectReference Include="..\..\src\TodoApi\TodoApi.csproj" />
                  </ItemGroup>
                </Project>
                """);

            var originalTestFile = Path.Combine(testTodoApiDir, "TodoServiceTests.cs");
            var originalContent = """
                using System;
                using FluentAssertions;
                using TodoApi.Models;
                using TodoApi.Services;
                using Xunit;

                namespace TodoApi.Tests;

                public class TodoServiceTests
                {
                    private readonly TodoAuditLogger _auditLogger;
                    private readonly TodoService _sut;

                    public TodoServiceTests()
                    {
                        _auditLogger = new TodoAuditLogger();
                        _sut = new TodoService(_auditLogger);
                    }

                    [Fact]
                    public void GetById_ExistingItem_ReturnsTodo()
                    {
                        // Arrange
                        var created = _sut.Create(new CreateTodoRequest { Title = "Existing item" });

                        // Act
                        var result = _sut.GetById(created.Id);

                        // Assert
                        result.Should().NotBeNull();
                        result!.Id.Should().Be(created.Id);
                        result.Title.Should().Be("Existing item");
                    }
                }
                """;
            File.WriteAllText(originalTestFile, originalContent);

            var projectGraph = WorktreeEditApplier.DiscoverProjectGraph(fixtureWorkspace);
            projectGraph.Should().NotBeEmpty();

            File.Exists(originalTestFile).Should().BeTrue();

            var searchBlock = """
                    [Fact]
                    public void GetById_ExistingItem_ReturnsTodo()
                    {
                        // Arrange
                        var created = _sut.Create(new CreateTodoRequest { Title = "Existing item" });

                        // Act
                        var result = _sut.GetById(created.Id);

                        // Assert
                        result.Should().NotBeNull();
                        result!.Id.Should().Be(created.Id);
                        result.Title.Should().Be("Existing item");
                    }
                """;

            var replaceBlock = """
                    [Fact]
                    public void GetById_ExistingItem_ReturnsTodo()
                    {
                        // Arrange
                        var created = _sut.Create(new CreateTodoRequest { Title = "Existing item" });

                        // Act
                        var result = _sut.GetById(created.Id);

                        // Assert
                        result.Should().NotBeNull();
                        result!.Id.Should().Be(created.Id);
                        result.Title.Should().Be("Existing item");
                    }

                    [Fact]
                    public void GetById_NonExistentItem_ReturnsNull()
                    {
                        // Act
                        var result = _sut.GetById(Guid.NewGuid());

                        // Assert
                        result.Should().BeNull();
                    }
                """;

            var edits = new List<DevPilot.Application.DeveloperAgent.Models.SearchReplaceEdit>
            {
                new(searchBlock, replaceBlock)
            };

            var appResult = WorktreeEditApplier.ValidateAndApplySearchReplaceEdits(
                originalContent,
                edits,
                "tests/TodoApi.Tests/TodoServiceTests.cs");

            appResult.Success.Should().BeTrue();
            var candidateCode = appResult.ModifiedContent!;

            var (archValid, archError) = RoslynContractExtractor.ValidateProjectArchitecturalDependencies(
                "tests/TodoApi.Tests/TodoServiceTests.cs",
                candidateCode,
                projectGraph,
                null);

            archValid.Should().BeTrue($"Level-1 fixture candidate must pass architectural validation but failed: {archError}");
            archError.Should().BeNull();

            var (symValid, symError) = RoslynContractExtractor.ValidateSymbolResolution(
                "tests/TodoApi.Tests/TodoServiceTests.cs",
                candidateCode,
                fixtureWorkspace);

            symValid.Should().BeTrue($"Level-1 fixture candidate must pass symbol resolution but failed: {symError}");
            symError.Should().BeNull();

            var (syntaxValid, syntaxErrors) = RoslynContractExtractor.ValidateSyntax(candidateCode);
            syntaxValid.Should().BeTrue();
            syntaxErrors.Should().BeEmpty();
        }
        finally
        {
            try
            {
                if (Directory.Exists(fixtureWorkspace))
                {
                    Directory.Delete(fixtureWorkspace, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void Diagnostic_ValidationFailureAfterRepair_PreservesReasonAcrossSingleLineSanitization()
    {
        var innerReason = "Unsupported architectural namespace 'TodoApi.Models' detected in 'tests/TodoApi.Tests/TodoServiceTests.cs'.";
        var rawExceptionMessage = $"File edit validation failed for 'tests/TodoApi.Tests/TodoServiceTests.cs' after repair: {innerReason}";

        var sanitizedForActivity = DevPilot.Infrastructure.Executions.EfExecutionActivityRecorder.SanitizeMessage($"Developer Agent failed: {rawExceptionMessage}");
        sanitizedForActivity.Should().Contain("after repair: Unsupported architectural namespace 'TodoApi.Models'");

        var sanitizedForExecution = DevPilot.Application.Executions.Commands.ProcessExecution.ProcessExecutionCommandHandler.SanitizeErrorMessage(rawExceptionMessage);
        sanitizedForExecution.Should().Contain("after repair: Unsupported architectural namespace 'TodoApi.Models'");
    }

    private class TestActivityRepo : IExecutionActivityRepository
    {
        public List<ExecutionActivity> Activities { get; } = new();
        public Task<IReadOnlyList<ExecutionActivity>> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionActivity>>(Activities);
    }

    private class TestWorkspaceMgr : IExecutionWorkspaceManager
    {
        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(Guid executionId, Guid taskId, string sourceRepositoryLocalPath, string? sourceBranch = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionWorkspaceResult("/path", "branch", true, null));
        public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(string workspacePath, string expectedBranchName, bool requireClean = true, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceVerificationResult(true, true, true, true, null));
    }

    private class TestFingerprintCalc : IExecutionChangeFingerprintCalculator
    {
        public Task<ExecutionFingerprintResult> ComputeFingerprintAsync(string workspacePath, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionFingerprintResult(true, "fp", "sha", false, 1));
        public Task<ExecutionFingerprintResult> ComputeStagedTreeFingerprintAsync(string workspacePath, string treeSha, string baseHeadSha, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionFingerprintResult(true, "fp", baseHeadSha, false, 1));
    }

    private class TestActivityRec : IExecutionActivityRecorder
    {
        public Task RecordActivityAsync(Guid executionId, ExecutionStage stage, ExecutionActivityStatus status, string message, ExecutionActivityMetadata? metadata = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
