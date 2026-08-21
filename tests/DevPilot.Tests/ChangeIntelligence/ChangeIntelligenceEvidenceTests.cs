using System.Text.Json;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.TaskImpactAnalysis.Commands.AnalyzeTaskImpact;
using DevPilot.Application.TaskImpactAnalysis.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using Xunit;

namespace DevPilot.Tests.ChangeIntelligence;

public sealed class ChangeIntelligenceEvidenceTests
{
    [Fact]
    public void ClassifyFileEvidence_IdentifiesControllerUsage()
    {
        var evidence = new RepositoryEvidenceProfile
        {
            ControllerFiles = new[] { "src/DevPilot.Api/Controllers/TasksController.cs" }
        };

        var (type, details, isUncertain) = ChangeIntelligenceEvidenceCollector.ClassifyFileEvidence(
            "src/DevPilot.Api/Controllers/TasksController.cs",
            ImpactFileChangeType.Modify,
            85,
            evidence);

        Assert.Equal("ControllerUsage", type);
        Assert.Contains("Controller endpoint", details);
        Assert.False(isUncertain);
    }

    [Fact]
    public void ClassifyFileEvidence_IdentifiesPersistenceAndMigrationRelationships()
    {
        var evidence = new RepositoryEvidenceProfile
        {
            PersistenceFiles = new[] { "src/DevPilot.Domain/Entities/Task.cs" },
            MigrationFiles = new[] { "src/DevPilot.Infrastructure/Persistence/Migrations/20260821_Initial.cs" }
        };

        var (pType, pDetails, pUncertain) = ChangeIntelligenceEvidenceCollector.ClassifyFileEvidence(
            "src/DevPilot.Domain/Entities/Task.cs",
            ImpactFileChangeType.Modify,
            90,
            evidence);

        Assert.Equal("PersistenceRelationship", pType);
        Assert.Contains("DbContext", pDetails);
        Assert.False(pUncertain);

        var (mType, mDetails, mUncertain) = ChangeIntelligenceEvidenceCollector.ClassifyFileEvidence(
            "src/DevPilot.Infrastructure/Persistence/Migrations/20260821_Initial.cs",
            ImpactFileChangeType.Add,
            80,
            evidence);

        Assert.Equal("MigrationRelationship", mType);
        Assert.Contains("migration", mDetails, StringComparison.OrdinalIgnoreCase);
        Assert.False(mUncertain);
    }

    [Fact]
    public void ClassifyFileEvidence_IdentifiesRelevantTestFiles()
    {
        var evidence = new RepositoryEvidenceProfile
        {
            TestFiles = new[] { "tests/DevPilot.Tests/TaskTests.cs" }
        };

        var (type, details, isUncertain) = ChangeIntelligenceEvidenceCollector.ClassifyFileEvidence(
            "tests/DevPilot.Tests/TaskTests.cs",
            ImpactFileChangeType.Modify,
            85,
            evidence);

        Assert.Equal("RelevantTest", type);
        Assert.Contains("test", details, StringComparison.OrdinalIgnoreCase);
        Assert.False(isUncertain);
    }

    [Fact]
    public void ClassifyFileEvidence_MarksLowConfidenceAsUncertain()
    {
        var evidence = new RepositoryEvidenceProfile();

        var (type, details, isUncertain) = ChangeIntelligenceEvidenceCollector.ClassifyFileEvidence(
            "src/DevPilot.Application/Services/Helper.cs",
            ImpactFileChangeType.Modify,
            60,
            evidence);

        Assert.Equal("Inferred", type);
        Assert.True(isUncertain);
    }

    [Fact]
    public void BuildChangeBrief_SynthesizesScopeRiskVerificationAndUnknowns()
    {
        var impactedFiles = new List<ImpactedFile>
        {
            new() { FilePath = "src/DevPilot.Api/Controllers/TasksController.cs", ChangeType = ImpactFileChangeType.Modify, Confidence = 90, EvidenceType = "ControllerUsage" },
            new() { FilePath = "src/DevPilot.Domain/Entities/Task.cs", ChangeType = ImpactFileChangeType.Modify, Confidence = 90, EvidenceType = "PersistenceRelationship" }
        };

        var risks = new List<Risk>
        {
            new() { Level = RiskLevel.High, Description = "API surface and schema changes combined" }
        };

        var evidence = new RepositoryEvidenceProfile
        {
            HasEfCore = true,
            HasTestProjects = true,
            VerificationProfile = new RepositoryProfile(
                State: RepositoryVerificationState.Configured,
                Ecosystems: new[] { ".NET" },
                Checks: new[]
                {
                    new RepositoryCheck(
                        Id: "dotnet-build",
                        DisplayName: "dotnet build",
                        Kind: RepositoryCheckKind.Build,
                        Ecosystem: ".NET",
                        Executable: "dotnet",
                        Arguments: new[] { "build" },
                        WorkingDirectory: ".",
                        Required: true,
                        Timeout: TimeSpan.FromMinutes(2),
                        Source: RepositoryCheckSource.DotNetManifest,
                        EvidencePath: "DevPilot.sln",
                        DiscoveryEvidence: "DevPilot.sln")
                },
                Message: null)
        };

        var brief = ChangeIntelligenceEvidenceCollector.BuildChangeBrief(
            impactedFiles,
            risks,
            new List<SystemImpact>(),
            new List<ChangeDimensionImpact>(),
            new List<string> { "Deployment ordering unknown" },
            evidence);

        Assert.Equal(2, brief.FileCount);
        Assert.True(brief.ProjectCount >= 1);
        Assert.Equal(RiskLevel.High, brief.RiskLevel);
        Assert.NotEmpty(brief.RiskReasons);
        Assert.Contains(brief.RiskReasons, r => r.Contains("API surface modified"));
        Assert.Contains(brief.RiskReasons, r => r.Contains("Database schema"));
        Assert.Single(brief.ExpectedChecks);
        Assert.Equal("dotnet build", brief.ExpectedChecks[0].DisplayName);
        Assert.Contains("Deployment ordering unknown", brief.Unknowns);
    }

    [Fact]
    public void SynthesizeUnknowns_AddsUnconfiguredVerificationAndMissingTests()
    {
        var evidence = new RepositoryEvidenceProfile
        {
            HasTestProjects = false,
            VerificationProfile = new RepositoryProfile(
                State: RepositoryVerificationState.Unconfigured,
                Ecosystems: Array.Empty<string>(),
                Checks: Array.Empty<RepositoryCheck>(),
                Message: "No test suite found")
        };

        var unknowns = ChangeIntelligenceEvidenceCollector.SynthesizeUnknowns(
            new List<string> { "External webhook contract unknown" },
            new List<ImpactedFile>(),
            new List<ChangeDimensionImpact>(),
            evidence);

        Assert.Contains("External webhook contract unknown", unknowns);
        Assert.Contains(unknowns, u => u.Contains("test project discovered", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(unknowns, u => u.Contains("unconfigured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChangeDimensions_SupportsAllStandardAreasAndNormalizes()
    {
        Assert.Equal("CODE", ChangeDimensionArea.Normalize("Code"));
        Assert.Equal("API", ChangeDimensionArea.Normalize("Api / Endpoints"));
        Assert.Equal("DATA", ChangeDimensionArea.Normalize("Database / Persistence"));
        Assert.Equal("TESTS", ChangeDimensionArea.Normalize("Unit Tests"));
        Assert.Equal("RUNTIME", ChangeDimensionArea.Normalize("Concurrency / Runtime"));
        Assert.Equal("DEPENDENCIES", ChangeDimensionArea.Normalize("Package Dependencies"));
        Assert.Equal("INFRASTRUCTURE", ChangeDimensionArea.Normalize("Docker / CI / Infrastructure"));
    }

    [Fact]
    public void TaskImpactAnalysis_EntityPersistence_JsonSerializationRoundtrip()
    {
        var resultData = new ImpactAnalysisResultData
        {
            Summary = "Test summary",
            Confidence = 95,
            ImpactedFiles = new List<ImpactedFile>
            {
                new()
                {
                    FilePath = "src/DevPilot.Domain/Entities/Task.cs",
                    ChangeType = ImpactFileChangeType.Modify,
                    Reason = "Entity updated",
                    Confidence = 95,
                    EvidenceType = "PersistenceRelationship",
                    EvidenceDetails = "Entity definition",
                    IsUncertain = false
                }
            },
            ChangeBrief = new ChangeBrief
            {
                FileCount = 1,
                ProjectCount = 1,
                RiskLevel = RiskLevel.Low,
                RiskReasons = new List<string> { "Bounded entity change" },
                ExpectedChecks = new List<ExpectedVerificationCheck>
                {
                    new() { CheckId = "build", DisplayName = "dotnet build", Kind = "Build", Required = true, Source = "Solution" }
                },
                Unknowns = new List<string> { "None" }
            },
            Dimensions = new List<ChangeDimensionImpact>
            {
                new()
                {
                    Area = "DATA",
                    ImpactLevel = SystemImpactLevel.Low,
                    Summary = "Schema change",
                    Details = new List<string> { "Added field" },
                    Evidence = new List<string> { "Task.cs" }
                }
            },
            Unknowns = new List<string> { "None" },
            RiskReasons = new List<string> { "Bounded entity change" }
        };

        var json = JsonSerializer.Serialize(resultData);
        var deserialized = JsonSerializer.Deserialize<ImpactAnalysisResultData>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Test summary", deserialized.Summary);
        Assert.Single(deserialized.ImpactedFiles);
        Assert.Equal("PersistenceRelationship", deserialized.ImpactedFiles[0].EvidenceType);
        Assert.NotNull(deserialized.ChangeBrief);
        Assert.Equal(RiskLevel.Low, deserialized.ChangeBrief.RiskLevel);
        Assert.Single(deserialized.Dimensions);
        Assert.Equal("DATA", deserialized.Dimensions[0].Area);
    }

    [Fact]
    public void DeveloperAgent_PromptHandoff_IsBoundedAndContainsGroundedEvidence()
    {
        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Add endpoint",
            TaskDescription: "Add a new endpoint",
            AcceptanceCriteria: "Must pass tests",
            ImpactAnalysisSummary: "Impact summary",
            ProposedPlan: "1. Add endpoint",
            ImpactedFilePaths: new[] { "src/DevPilot.Api/Controllers/TasksController.cs" },
            WorkspacePath: "C:/fake/workspace",
            BranchName: "feature/test",
            ImpactedFiles: new[]
            {
                new ImpactedFileDetail("src/DevPilot.Api/Controllers/TasksController.cs", "Modify", "Add endpoint", "ControllerUsage", false)
            },
            ChangeDimensions: new[] { "API: Controller endpoint updated" },
            ExpectedChecks: new[] { "dotnet build" },
            Unknowns: new[] { "External auth behavior unconfirmed" });

        var contextFiles = new Dictionary<string, string>
        {
            ["src/DevPilot.Api/Controllers/TasksController.cs"] = "// Controller code"
        };

        var userPrompt = DevPilot.Infrastructure.DeveloperAgent.DeveloperAgent.BuildManifestUserPrompt(
            request,
            contextFiles,
            Array.Empty<DiscoveredProjectNode>());

        Assert.Contains("=== Predicted Impacted Files ===", userPrompt);
        Assert.Contains("Evidence: ControllerUsage", userPrompt);
        Assert.Contains("=== Change Dimensions ===", userPrompt);
        Assert.Contains("=== Unknowns / Boundaries ===", userPrompt);
        Assert.True(userPrompt.Length < 3000, "DeveloperAgent prompt must remain bounded and compact.");
    }

    [Fact]
    public void ChangeBrief_SurfacesProbabilisticMigrationStatement_WhenEfCorePresent()
    {
        var evidence = new RepositoryEvidenceProfile
        {
            HasEfCore = true,
            PersistenceFiles = new[] { "src/DevPilot.Domain/Entities/User.cs" },
            MigrationFiles = new[] { "src/DevPilot.Infrastructure/Persistence/Migrations/20260101_Init.cs" }
        };

        var files = new List<ImpactedFile>
        {
            new() { FilePath = "src/DevPilot.Domain/Entities/User.cs", ChangeType = ImpactFileChangeType.Modify, Confidence = 90, EvidenceType = "PersistenceRelationship" }
        };

        var brief = ChangeIntelligenceEvidenceCollector.BuildChangeBrief(
            files,
            new List<Risk>(),
            new List<SystemImpact>(),
            new List<ChangeDimensionImpact>(),
            new List<string>(),
            evidence);

        Assert.NotNull(brief.DataSummary);
        Assert.Contains("migration likely/expected", brief.DataSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseStructuredResult_CompactOrderSystemPayload_FitsBudgetAndPopulatesAllIntelligence()
    {
        var compactOrderSystemJson = """
        {
          "summary": "Add order cancellation endpoint with inventory restock and EF Core update.",
          "confidence": 90,
          "impactedFiles": [
            { "filePath": "src/DevPilot.Api/Controllers/OrdersController.cs", "changeType": "Modify", "reason": "Add CancelOrder action" },
            { "filePath": "src/DevPilot.Domain/Entities/Order.cs", "changeType": "Modify", "reason": "Add Cancel domain logic" },
            { "filePath": "src/DevPilot.Application/Orders/Commands/CancelOrderCommand.cs", "changeType": "Add", "reason": "Handler for cancellation" },
            { "filePath": "src/DevPilot.Infrastructure/Persistence/Migrations/20260821_CancelOrder.cs", "changeType": "Add", "reason": "Migration for cancellation timestamp" },
            { "filePath": "tests/DevPilot.Tests/Orders/CancelOrderTests.cs", "changeType": "Add", "reason": "Unit tests" }
          ],
          "dimensions": [
            { "area": "API", "impactLevel": "Medium", "summary": "New cancellation endpoint" },
            { "area": "DATA", "impactLevel": "Medium", "summary": "Add status field to Orders table" }
          ],
          "proposedPlan": [
            { "order": 1, "title": "Implement domain and command", "description": "Add Cancel logic to Order entity and implement handler" },
            { "order": 2, "title": "Add controller endpoint and tests", "description": "Expose POST /api/orders/{id}/cancel and write tests" }
          ],
          "risks": [
            { "level": "Medium", "description": "Concurrent cancellation race condition" }
          ],
          "unknowns": [
            "Payment gateway refund webhook SLA"
          ]
        }
        """;

        // Verify that the payload is ultra-compact (< 1500 chars / ~350 tokens)
        Assert.True(compactOrderSystemJson.Length < 1500, $"Payload size was {compactOrderSystemJson.Length} characters, which exceeds the compact budget.");

        var projectGraph = new List<DiscoveredProjectNode>
        {
            new() { ProjectPath = "src/DevPilot.Api/DevPilot.Api.csproj", ProjectName = "DevPilot.Api", ProjectDirectory = "src/DevPilot.Api", PackageReferences = new() { "Microsoft.EntityFrameworkCore" }, IsTestProject = false },
            new() { ProjectPath = "src/DevPilot.Domain/DevPilot.Domain.csproj", ProjectName = "DevPilot.Domain", ProjectDirectory = "src/DevPilot.Domain", IsTestProject = false },
            new() { ProjectPath = "src/DevPilot.Application/DevPilot.Application.csproj", ProjectName = "DevPilot.Application", ProjectDirectory = "src/DevPilot.Application", IsTestProject = false },
            new() { ProjectPath = "src/DevPilot.Infrastructure/DevPilot.Infrastructure.csproj", ProjectName = "DevPilot.Infrastructure", ProjectDirectory = "src/DevPilot.Infrastructure", PackageReferences = new() { "Microsoft.EntityFrameworkCore" }, IsTestProject = false },
            new() { ProjectPath = "tests/DevPilot.Tests/DevPilot.Tests.csproj", ProjectName = "DevPilot.Tests", ProjectDirectory = "tests/DevPilot.Tests", IsTestProject = true }
        };

        var evidence = new RepositoryEvidenceProfile
        {
            ProjectGraph = projectGraph,
            ProjectRoots = new[] { "src/DevPilot.Api", "src/DevPilot.Domain", "src/DevPilot.Application", "src/DevPilot.Infrastructure", "tests/DevPilot.Tests" },
            HasEfCore = true,
            HasTestProjects = true,
            ControllerFiles = new[] { "src/DevPilot.Api/Controllers/OrdersController.cs" },
            PersistenceFiles = new[] { "src/DevPilot.Domain/Entities/Order.cs" },
            InventoryCsFiles = new[] { "src/DevPilot.Api/Controllers/OrdersController.cs", "src/DevPilot.Domain/Entities/Order.cs" },
            VerificationProfile = new RepositoryProfile(
                State: RepositoryVerificationState.Configured,
                Ecosystems: new[] { ".NET" },
                Checks: new[]
                {
                    new RepositoryCheck("build", "dotnet build", RepositoryCheckKind.Build, ".NET", "dotnet", new[] { "build" }, ".", true, TimeSpan.FromMinutes(2), RepositoryCheckSource.DotNetManifest, "DevPilot.sln")
                })
        };

        var parseResult = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(
            compactOrderSystemJson,
            evidence,
            workspaceLocalPath: "");

        Assert.True(parseResult.Success, parseResult.ErrorMessage);
        var data = parseResult.ResultData!;

        Assert.Equal(5, data.ImpactedFiles.Count);
        Assert.Equal("ControllerUsage", data.ImpactedFiles[0].EvidenceType);
        Assert.Equal("PersistenceRelationship", data.ImpactedFiles[1].EvidenceType);
        Assert.Equal("Inferred", data.ImpactedFiles[2].EvidenceType);
        Assert.Equal("MigrationRelationship", data.ImpactedFiles[3].EvidenceType);
        Assert.Equal("RelevantTest", data.ImpactedFiles[4].EvidenceType);

        Assert.NotNull(data.ChangeBrief);
        Assert.Equal(5, data.ChangeBrief.FileCount);
        Assert.True(data.ChangeBrief.ProjectCount >= 3);
        Assert.NotEmpty(data.ChangeBrief.RiskReasons);
        Assert.Single(data.ChangeBrief.ExpectedChecks);

        Assert.NotEmpty(data.Dimensions);
        Assert.Contains(data.Dimensions, d => d.Area == "API");
        Assert.Contains(data.Dimensions, d => d.Area == "DATA");

        Assert.NotEmpty(data.SystemImpacts);
        Assert.NotEmpty(data.ProposedPlan);
        Assert.NotEmpty(data.Risks);
        Assert.NotEmpty(data.Unknowns);
        Assert.Contains("Payment gateway refund webhook SLA", data.Unknowns);
    }

    [Fact]
    public void TryParseStructuredResult_MinimalPayload_GracefullyEnrichesFromRepositoryEvidence()
    {
        var minimalJson = """
        {
          "summary": "Fix validation in orders controller",
          "confidence": 85,
          "impactedFiles": [
            { "filePath": "src/DevPilot.Api/Controllers/OrdersController.cs", "changeType": "Modify" }
          ]
        }
        """;

        var evidence = new RepositoryEvidenceProfile
        {
            ProjectRoots = new[] { "src/DevPilot.Api" },
            ControllerFiles = new[] { "src/DevPilot.Api/Controllers/OrdersController.cs" },
            InventoryCsFiles = new[] { "src/DevPilot.Api/Controllers/OrdersController.cs" },
            HasEfCore = false,
            HasTestProjects = false,
            VerificationProfile = new RepositoryProfile(
                State: RepositoryVerificationState.Unconfigured,
                Ecosystems: Array.Empty<string>(),
                Checks: Array.Empty<RepositoryCheck>())
        };

        var parseResult = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(
            minimalJson,
            evidence,
            workspaceLocalPath: "");

        Assert.True(parseResult.Success, parseResult.ErrorMessage);
        var data = parseResult.ResultData!;

        Assert.Single(data.ImpactedFiles);
        Assert.Equal("ControllerUsage", data.ImpactedFiles[0].EvidenceType);
        Assert.NotNull(data.ChangeBrief);
        Assert.NotEmpty(data.ProposedPlan);
        Assert.NotEmpty(data.Dimensions);
        Assert.NotEmpty(data.Unknowns);
        Assert.Contains(data.Unknowns, u => u.Contains("unconfigured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfidenceCalibration_StrongGroundedEvidenceWithoutModelConfidence_DoesNotBecomeZero()
    {
        var evidence = new RepositoryEvidenceProfile
        {
            ControllerFiles = new[] { "src/DevPilot.Api/Controllers/UsersController.cs" },
            PersistenceFiles = new[] { "src/DevPilot.Domain/Entities/User.cs" },
            MigrationFiles = new[] { "src/DevPilot.Infrastructure/Migrations/20260821_AddUser.cs" },
            TestFiles = new[] { "tests/DevPilot.Tests/UserTests.cs" },
            InventoryCsFiles = new[]
            {
                "src/DevPilot.Api/Controllers/UsersController.cs",
                "src/DevPilot.Domain/Entities/User.cs",
                "src/DevPilot.Domain/Contracts/IUserRepository.cs",
                "src/DevPilot.Infrastructure/Migrations/20260821_AddUser.cs",
                "tests/DevPilot.Tests/UserTests.cs",
                "src/DevPilot.Application/Common/StringExtensions.cs"
            }
        };

        // 1. ControllerUsage without model confidence
        var (_, _, _, ctrlConfidence) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
            "src/DevPilot.Api/Controllers/UsersController.cs",
            ImpactFileChangeType.Modify,
            null,
            evidence);
        Assert.Equal(90, ctrlConfidence);

        // 2. PersistenceRelationship without model confidence
        var (_, _, _, persistConfidence) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
            "src/DevPilot.Domain/Entities/User.cs",
            ImpactFileChangeType.Modify,
            0,
            evidence);
        Assert.Equal(90, persistConfidence);

        // 3. MigrationRelationship without model confidence
        var (_, _, _, migConfidence) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
            "src/DevPilot.Infrastructure/Migrations/20260821_AddUser.cs",
            ImpactFileChangeType.Modify,
            null,
            evidence);
        Assert.Equal(90, migConfidence);

        // 4. InterfaceImplementation without model confidence
        var (_, _, _, ifaceConfidence) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
            "src/DevPilot.Domain/Contracts/IUserRepository.cs",
            ImpactFileChangeType.Modify,
            null,
            evidence);
        Assert.Equal(85, ifaceConfidence);

        // 5. RelevantTest without model confidence
        var (_, _, _, testConfidence) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
            "tests/DevPilot.Tests/UserTests.cs",
            ImpactFileChangeType.Modify,
            null,
            evidence);
        Assert.Equal(85, testConfidence);

        // 6. SymbolReference without model confidence
        var (_, _, _, symConfidence) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
            "src/DevPilot.Application/Common/StringExtensions.cs",
            ImpactFileChangeType.Modify,
            null,
            evidence);
        Assert.Equal(75, symConfidence);
    }

    [Fact]
    public void ConfidenceCalibration_InferredAndSpeculativeEvidence_RemainsLower()
    {
        var evidence = new RepositoryEvidenceProfile
        {
            ProjectRoots = new[] { "src/DevPilot.Application" }
        };

        // Newly added file in valid project root -> medium range (60%)
        var (newType, _, newUncertain, newConfidence) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
            "src/DevPilot.Application/Orders/NewCommand.cs",
            ImpactFileChangeType.Add,
            null,
            evidence);
        Assert.Equal("Inferred", newType);
        Assert.False(newUncertain);
        Assert.Equal(60, newConfidence);

        // Speculative file not found in inventory for Modify -> low range (40%) and uncertain
        var (specType, _, specUncertain, specConfidence) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
            "src/DevPilot.Application/Services/NonExistentService.cs",
            ImpactFileChangeType.Modify,
            null,
            evidence);
        Assert.Equal("Inferred", specType);
        Assert.True(specUncertain);
        Assert.Equal(40, specConfidence);
    }

    [Fact]
    public void ConfidenceCalibration_ValidModelConfidence_IsPreservedWhenGrounded()
    {
        var evidence = new RepositoryEvidenceProfile
        {
            ControllerFiles = new[] { "src/DevPilot.Api/Controllers/OrdersController.cs" },
            InventoryCsFiles = new[] { "src/DevPilot.Api/Controllers/OrdersController.cs" }
        };

        var (_, _, _, preservedConfidence) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
            "src/DevPilot.Api/Controllers/OrdersController.cs",
            ImpactFileChangeType.Modify,
            95,
            evidence);

        Assert.Equal(95, preservedConfidence);
    }

    [Fact]
    public void ConfidenceCalibration_InvalidOrOutOfRangeConfidence_IsNormalized()
    {
        var evidence = new RepositoryEvidenceProfile
        {
            ControllerFiles = new[] { "src/DevPilot.Api/Controllers/OrdersController.cs" },
            InventoryCsFiles = new[] { "src/DevPilot.Api/Controllers/OrdersController.cs" }
        };

        // Out of range > 100 -> clamped to deterministic baseline or 100
        var (_, _, _, highClamped) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
            "src/DevPilot.Api/Controllers/OrdersController.cs",
            ImpactFileChangeType.Modify,
            180,
            evidence);
        Assert.True(highClamped <= 100 && highClamped >= 0);

        // Out of range < 0 -> normalized to baseline
        var (_, _, _, lowNormalized) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
            "src/DevPilot.Api/Controllers/OrdersController.cs",
            ImpactFileChangeType.Modify,
            -50,
            evidence);
        Assert.Equal(90, lowNormalized);
    }

    [Fact]
    public void ConfidenceCalibration_IsUncertain_RemainsIndependentFromConfidence()
    {
        var evidence = new RepositoryEvidenceProfile
        {
            PersistenceFiles = new[] { "src/DevPilot.Domain/Entities/Order.cs" },
            InventoryCsFiles = new[] { "src/DevPilot.Domain/Entities/Order.cs" }
        };

        // Grounded persistence file has IsUncertain = false regardless of confidence
        var (_, _, isUncertain, _) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
            "src/DevPilot.Domain/Entities/Order.cs",
            ImpactFileChangeType.Modify,
            50,
            evidence);
        Assert.False(isUncertain);

        // Speculative file without inventory match has IsUncertain = true even if model claims high confidence
        var (_, _, specUncertain, specConfidence) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
            "src/DevPilot.Domain/Entities/NonExistent.cs",
            ImpactFileChangeType.Modify,
            99,
            new RepositoryEvidenceProfile());
        Assert.True(specUncertain);
        Assert.True(specConfidence <= 50, "Speculative file confidence must be bounded even if model claimed 99%");
    }

    [Fact]
    public void TryParseStructuredResult_GroundedFilesWithoutModelConfidence_HaveNonZeroConfidence()
    {
        var jsonWithoutFileConfidence = """
        {
          "summary": "Update order status handling",
          "impactedFiles": [
            { "filePath": "src/DevPilot.Api/Controllers/OrdersController.cs", "changeType": "Modify", "reason": "Update status endpoint" },
            { "filePath": "src/DevPilot.Domain/Entities/Order.cs", "changeType": "Modify", "reason": "Add state transition" }
          ]
        }
        """;

        var evidence = new RepositoryEvidenceProfile
        {
            ProjectRoots = new[] { "src/DevPilot.Api", "src/DevPilot.Domain" },
            ControllerFiles = new[] { "src/DevPilot.Api/Controllers/OrdersController.cs" },
            PersistenceFiles = new[] { "src/DevPilot.Domain/Entities/Order.cs" },
            InventoryCsFiles = new[] { "src/DevPilot.Api/Controllers/OrdersController.cs", "src/DevPilot.Domain/Entities/Order.cs" }
        };

        var parseResult = AnalyzeTaskImpactCommandHandler.TryParseStructuredResult(
            jsonWithoutFileConfidence,
            evidence,
            workspaceLocalPath: "");

        Assert.True(parseResult.Success);
        var data = parseResult.ResultData!;
        Assert.Equal(2, data.ImpactedFiles.Count);
        Assert.Equal(90, data.ImpactedFiles[0].Confidence);
        Assert.Equal(90, data.ImpactedFiles[1].Confidence);
        Assert.Equal(90, data.Confidence);
    }
}
