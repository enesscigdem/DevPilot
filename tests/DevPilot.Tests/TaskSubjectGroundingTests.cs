using DevPilot.Application.TaskImpactAnalysis.Commands.AnalyzeTaskImpact;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.TaskImpactAnalysis.Services;
using DevPilot.Application.Tasks.Commands.ApproveTask;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure.DatabaseIntelligence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public class TaskSubjectGroundingTests
{
    private readonly RepositoryEvidenceProfile _orderOnlyEvidence;
    private readonly IDatabaseImpactAnalyzer _databaseImpactAnalyzer;

    public TaskSubjectGroundingTests()
    {
        _orderOnlyEvidence = new RepositoryEvidenceProfile(
            ProjectRoots: new List<string> { "src/DevPilot.Domain", "src/DevPilot.Application", "src/DevPilot.Infrastructure" },
            InventoryCsFiles: new List<string>
            {
                "src/DevPilot.Domain/Entities/Order.cs",
                "src/DevPilot.Domain/Entities/Coupon.cs",
                "src/DevPilot.Infrastructure/Persistence/OrderDbContext.cs"
            },
            PersistenceFiles: new List<string>
            {
                "src/DevPilot.Domain/Entities/Order.cs",
                "src/DevPilot.Domain/Entities/Coupon.cs",
                "src/DevPilot.Infrastructure/Persistence/OrderDbContext.cs"
            },
            HasEfCore: true,
            HasTestProjects: true
        );

        _databaseImpactAnalyzer = new EfCoreDatabaseImpactAnalyzer();
    }

    [Fact]
    public void Rule1_ExplicitMissingEntity_TurkishPrompt_BlocksApproval_AndSurfacesUnresolvedMessage()
    {
        // Real acceptance gap prompt: Customer entity is requested, but only Order.cs exists in repository
        var prompt = "Customer entity’sindeki Email alanını zorunlu hale getirelim ve maksimum uzunluğunu 500’den 200’e düşürelim.";

        var groundingResult = TaskSubjectGroundingValidator.Validate(prompt, _orderOnlyEvidence, null);

        groundingResult.IsGrounded.Should().BeFalse();
        groundingResult.TargetSubject.Should().Be("Customer.Email");
        groundingResult.UnresolvedReason.Should().Be("Customer.Email could not be resolved in repository evidence.");
    }

    [Fact]
    public void Rule1_ExplicitMissingEntity_EnglishPrompt_BlocksApproval()
    {
        var prompt = "Make Customer.Email required and reduce max length from 500 to 200";

        var groundingResult = TaskSubjectGroundingValidator.Validate(prompt, _orderOnlyEvidence, null);

        groundingResult.IsGrounded.Should().BeFalse();
        groundingResult.TargetSubject.Should().Be("Customer.Email");
        groundingResult.UnresolvedReason.Should().Be("Customer.Email could not be resolved in repository evidence.");
    }

    [Fact]
    public void Rule2_ExistingEntity_MissingExplicitProperty_BlocksApproval()
    {
        // Order exists, but NonExistentField does not exist on Order
        var prompt = "Make Order.NonExistentField required and reduce max length from 500 to 200";

        // Create temporary workspace with Order.cs without NonExistentField
        var tempDir = Path.Combine(Path.GetTempPath(), "DevPilot_GroundingTest_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var orderPath = Path.Combine(tempDir, "Order.cs");
            File.WriteAllText(orderPath, "public class Order { public Guid Id { get; set; } public decimal TotalAmount { get; set; } }");

            var evidence = new RepositoryEvidenceProfile(
                InventoryCsFiles: new List<string> { "Order.cs" },
                PersistenceFiles: new List<string> { "Order.cs" }
            );

            var groundingResult = TaskSubjectGroundingValidator.Validate(prompt, evidence, tempDir);

            groundingResult.IsGrounded.Should().BeFalse();
            groundingResult.TargetSubject.Should().Be("Order.NonExistentField");
            groundingResult.UnresolvedReason.Should().Be("Order.NonExistentField could not be resolved in repository evidence.");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Rule3_ExistingGroundedEntity_AdditionIntent_AllowsNormalPlan()
    {
        // Add optional DiscountAmount to Order entity -> Order exists, addition of new property is allowed
        var prompt = "Add optional DiscountAmount to Order entity";

        var groundingResult = TaskSubjectGroundingValidator.Validate(prompt, _orderOnlyEvidence, null);

        groundingResult.IsGrounded.Should().BeTrue();
        groundingResult.TargetEntity.Should().Be("Order");
        groundingResult.TargetProperty.Should().Be("DiscountAmount");
    }

    [Fact]
    public void Rule4_ModelProposesUnrelatedSubstituteEntity_IsRejected_AndPlanCleared()
    {
        var prompt = "Customer entity’sindeki Email alanını zorunlu hale getirelim ve maksimum uzunluğunu 500’den 200’e düşürelim.";

        // Model hallucinates Order.cs and OrderDbContext.cs as substitute files
        var aiResponse = @"{
            ""summary"": ""Make email required and reduce length"",
            ""confidence"": 70,
            ""impactedFiles"": [
                { ""filePath"": ""src/DevPilot.Domain/Entities/Order.cs"", ""changeType"": ""Modify"", ""reason"": ""Update customer email field"", ""confidence"": 80 },
                { ""filePath"": ""src/DevPilot.Infrastructure/Persistence/OrderDbContext.cs"", ""changeType"": ""Modify"", ""reason"": ""Update configuration"", ""confidence"": 75 }
            ],
            ""proposedPlan"": [
                { ""order"": 1, ""title"": ""Modify Order.cs"", ""description"": ""Add required Email attribute"" }
            ],
            ""risks"": [
                { ""level"": ""High"", ""description"": ""Non-nullable email without default"" }
            ]
        }";

        var parseResult = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(
            aiResponse,
            _orderOnlyEvidence,
            null,
            prompt,
            _databaseImpactAnalyzer);

        parseResult.Success.Should().BeTrue();
        var resultData = parseResult.ResultData!;

        // Grounding must be flagged as unresolved
        resultData.IsGroundingUnresolved.Should().BeTrue();
        resultData.UnresolvedSubject.Should().Be("Customer.Email");
        resultData.UnresolvedReason.Should().Be("Customer.Email could not be resolved in repository evidence.");

        // Unrelated substitute files must be discarded / rejected
        resultData.ImpactedFiles.Should().BeEmpty();
        resultData.ProposedPlan.Should().BeEmpty();

        // Executable confidence must drop to 0
        resultData.Confidence.Should().Be(0);

        // Blocking reasons must appear in RiskReasons and Unknowns
        resultData.RiskReasons.Should().Contain("Customer.Email could not be resolved in repository evidence.");
        resultData.Unknowns.Should().Contain("Customer.Email could not be resolved in repository evidence.");
    }

    [Fact]
    public void Rule5_AdvisoryDatabaseImpact_RemainsVisible_WhileExecutablePlanIsBlocked()
    {
        var prompt = "Customer entity’sindeki Email alanını zorunlu hale getirelim ve maksimum uzunluğunu 500’den 200’e düşürelim.";

        var aiResponse = @"{
            ""summary"": ""Make email required and reduce length"",
            ""confidence"": 70,
            ""impactedFiles"": [
                { ""filePath"": ""src/DevPilot.Domain/Entities/Order.cs"", ""changeType"": ""Modify"", ""reason"": ""Update email"", ""confidence"": 80 }
            ]
        }";

        var parseResult = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(
            aiResponse,
            _orderOnlyEvidence,
            null,
            prompt,
            _databaseImpactAnalyzer);

        parseResult.Success.Should().BeTrue();
        var resultData = parseResult.ResultData!;

        // Advisory Database Impact must remain populated and accurate
        resultData.DatabaseImpact.Should().NotBeNull();
        resultData.DatabaseImpact!.RequiresSchemaMigration.Should().BeTrue();
        resultData.DatabaseImpact.DataRiskLevel.Should().Be(RiskLevel.High);
        resultData.DatabaseImpact.DataMigrationRequirement.Should().Be(DataMigrationRequirement.ReviewRequired);
        resultData.DatabaseImpact.Changes.Should().Contain(c => c.ObjectName == "Email" && c.Risk == RiskLevel.High);

        // But executable plan and approval are blocked
        resultData.IsGroundingUnresolved.Should().BeTrue();
        resultData.ImpactedFiles.Should().BeEmpty();
        resultData.Confidence.Should().Be(0);
    }

    [Fact]
    public async Task Rule6_ApproveTaskCommandHandler_BlocksApproval_WhenGroundingIsUnresolved()
    {
        var taskId = Guid.NewGuid();
        var task = new DevelopmentTask
        {
            Id = taskId,
            Title = "Customer email update",
            Status = DevelopmentTaskStatus.ReadyForAnalysis,
            RepositoryWorkspaceId = Guid.NewGuid()
        };

        var analysis = new DevPilot.Domain.Entities.TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskId,
            Status = ImpactAnalysisStatus.Completed,
            Confidence = 0,
            StructuredResult = new ImpactAnalysisResultData
            {
                IsGroundingUnresolved = true,
                UnresolvedSubject = "Customer.Email",
                UnresolvedReason = "Customer.Email could not be resolved in repository evidence.",
                DatabaseImpact = new DatabaseImpact
                {
                    RequiresSchemaMigration = true,
                    DataRiskLevel = RiskLevel.High,
                    DataMigrationRequirement = DataMigrationRequirement.ReviewRequired
                }
            }
        };

        var fakeTaskRepo = new FakeTaskRepository(task);
        var fakeAnalysisRepo = new FakeAnalysisRepository(analysis);
        var handler = new ApproveTaskCommandHandler(fakeTaskRepo, fakeAnalysisRepo, NullLogger<ApproveTaskCommandHandler>.Instance);

        // When task is in ReadyForAnalysis, cannot approve
        var result1 = await handler.HandleAsync(new ApproveTaskCommand(taskId));
        result1.Success.Should().BeFalse();
        result1.Conflict.Should().BeTrue();

        // Even if task status was somehow set to AwaitingApproval, approval handler explicitly blocks ungrounded analysis
        task.Status = DevelopmentTaskStatus.AwaitingApproval;
        var result2 = await handler.HandleAsync(new ApproveTaskCommand(taskId));
        result2.Success.Should().BeFalse();
        result2.Conflict.Should().BeTrue();
        result2.ErrorMessage.Should().Contain("Customer.Email could not be resolved in repository evidence.");
    }

    [Fact]
    public void Regression1_MissingCustomerEmail_StillBlocks()
    {
        var prompt = "Customer entity'sindeki Email alanını zorunlu yap";
        var result = TaskSubjectGroundingValidator.Validate(prompt, _orderOnlyEvidence, null);

        result.IsGrounded.Should().BeFalse();
        result.TargetSubject.Should().Be("Customer.Email");
        result.UnresolvedReason.Should().Be("Customer.Email could not be resolved in repository evidence.");
    }

    [Fact]
    public void Regression2_MissingOrderServiceSomeMethod_Blocks_WhenOrderServiceIsRepoOwned()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DevPilot_GroundingTest_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var servicePath = Path.Combine(tempDir, "OrderService.cs");
            File.WriteAllText(servicePath, "public class OrderService { public void ProcessOrder() { } }");

            var evidence = new RepositoryEvidenceProfile(
                InventoryCsFiles: new List<string> { "OrderService.cs" },
                PersistenceFiles: new List<string>()
            );

            var prompt = "OrderService servisindeki CalculateTotal metodunu değiştir";
            var result = TaskSubjectGroundingValidator.Validate(prompt, evidence, tempDir);

            result.IsGrounded.Should().BeFalse();
            result.TargetSubject.Should().Be("OrderService.CalculateTotal");
            result.UnresolvedReason.Should().Be("OrderService.CalculateTotal could not be resolved in repository evidence.");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Regression3_ConfigurationGetConnectionString_DoesNotBlock()
    {
        var prompt = "Configure ConnectionStrings via Configuration.GetConnectionString in Program.cs";
        var result = TaskSubjectGroundingValidator.Validate(prompt, _orderOnlyEvidence, null);

        result.IsGrounded.Should().BeTrue();
        result.UnresolvedReason.Should().BeNull();
    }

    [Fact]
    public void Regression4_AddDbContext_DoesNotBlock()
    {
        var prompt = "Register OrderDbContext using services.AddDbContext in Program.cs";
        var result = TaskSubjectGroundingValidator.Validate(prompt, _orderOnlyEvidence, null);

        result.IsGrounded.Should().BeTrue();
        result.UnresolvedReason.Should().BeNull();
    }

    [Fact]
    public void Regression5_UseAuthenticationAndUseAuthorization_DoNotBlock()
    {
        var prompt = "Add app.UseAuthentication and app.UseAuthorization to the middleware pipeline";
        var result = TaskSubjectGroundingValidator.Validate(prompt, _orderOnlyEvidence, null);

        result.IsGrounded.Should().BeTrue();
        result.UnresolvedReason.Should().BeNull();
    }

    [Fact]
    public void Regression6_TurkishHerTipinde_DoesNotBecomeDottedSubject_AndDoesNotBlock()
    {
        var prompt = "Veritabanındaki her tipinde ve tablolarda index yapılandırması kontrol edilmeli.";
        var result = TaskSubjectGroundingValidator.Validate(prompt, _orderOnlyEvidence, null);

        result.IsGrounded.Should().BeTrue();
        result.UnresolvedReason.Should().BeNull();
    }

    [Fact]
    public void Regression7_OrdinaryProseWithPunctuation_DoesNotProduceBlockingSubjects()
    {
        var prompt = "Please check the status. And make sure everything works correctly.";
        var result = TaskSubjectGroundingValidator.Validate(prompt, _orderOnlyEvidence, null);

        result.IsGrounded.Should().BeTrue();
        result.UnresolvedReason.Should().BeNull();
    }

    [Fact]
    public void Regression8_ProjectBrain_CustomWebApplicationFactory_RemainsExecutable()
    {
        var prompt = "Update CustomWebApplicationFactory to configure test services and in-memory options";
        var result = TaskSubjectGroundingValidator.Validate(prompt, _orderOnlyEvidence, null);

        result.IsGrounded.Should().BeTrue();
        result.UnresolvedReason.Should().BeNull();
    }

    [Fact]
    public void Regression9_RedisAndConnectionStringConfig_RemainsExecutable()
    {
        var prompt = "Add Redis caching configuration and connection string loading in Program.cs";
        var result = TaskSubjectGroundingValidator.Validate(prompt, _orderOnlyEvidence, null);

        result.IsGrounded.Should().BeTrue();
        result.UnresolvedReason.Should().BeNull();
    }

    [Fact]
    public void Regression10_DatabaseImpact_NotTriggered_ForTestFactoryAndDiTasks()
    {
        var prompt = "Update CustomWebApplicationFactory with AddDbContext for test services";
        var impact = _databaseImpactAnalyzer.AnalyzeImpact(
            new List<ImpactedFile>
            {
                new() { FilePath = "tests/DevPilot.Tests/CustomWebApplicationFactory.cs", ChangeType = ImpactFileChangeType.Modify }
            },
            new List<ChangeDimensionImpact>(),
            new List<Risk>(),
            _orderOnlyEvidence,
            prompt,
            null);

        impact.RequiresSchemaMigration.Should().BeFalse();
        impact.Changes.Should().BeEmpty();
        impact.DataRiskLevel.Should().Be(RiskLevel.Low);
    }

    private class FakeTaskRepository : ITaskRepository
    {
        private readonly DevelopmentTask _task;
        public FakeTaskRepository(DevelopmentTask task) => _task = task;
        public Task<DevelopmentTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<DevelopmentTask?>(_task.Id == id ? _task : null);
        public Task AddAsync(DevelopmentTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(DevelopmentTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(DevelopmentTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DevelopmentTask>> GetAllAsync(DevelopmentTaskQueryFilter filter, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DevelopmentTask>>(new List<DevelopmentTask> { _task });
    }

    private class FakeAnalysisRepository : IImpactAnalysisRepository
    {
        private readonly DevPilot.Domain.Entities.TaskImpactAnalysis _analysis;
        public FakeAnalysisRepository(DevPilot.Domain.Entities.TaskImpactAnalysis analysis) => _analysis = analysis;
        public Task<DevPilot.Domain.Entities.TaskImpactAnalysis?> GetLatestByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default)
            => Task.FromResult<DevPilot.Domain.Entities.TaskImpactAnalysis?>(_analysis.DevelopmentTaskId == taskId ? _analysis : null);
        public Task AddAsync(DevPilot.Domain.Entities.TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(DevPilot.Domain.Entities.TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> StartAnalysisAtomicAsync(DevPilot.Domain.Entities.TaskImpactAnalysis analysis, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> HasActiveAnalysisForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> ReconcileStaleAnalysesAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
