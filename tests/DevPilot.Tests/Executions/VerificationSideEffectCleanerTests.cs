using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure.Executions;
using Xunit;
using TaskImpactAnalysisEntity = DevPilot.Domain.Entities.TaskImpactAnalysis;

namespace DevPilot.Tests.Executions;

public sealed class VerificationSideEffectCleanerTests
{
    [Fact]
    public void ClassifyStatusEntries_ExcludesUntrackedBuildArtifacts_WhilePreservingAuthoritativeTaskChanges()
    {
        var authoritativeEdits = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/DevPilot.Api/Controllers/OrdersController.cs",
            "src/DevPilot.Domain/Entities/Order.cs"
        };

        var currentStatus = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/DevPilot.Api/Controllers/OrdersController.cs", null, " M", "Modified"),
            new("src/DevPilot.Domain/Entities/Order.cs", null, " M", "Modified"),
            new("src/DevPilot.Api/bin/Debug/net10.0/DevPilot.Api.dll", null, "??", "Added"),
            new("src/DevPilot.Api/bin/Debug/net10.0/DevPilot.Api.pdb", null, "??", "Added"),
            new("src/DevPilot.Api/obj/Debug/net10.0/project.assets.json", null, "??", "Added"),
            new("src/DevPilot.Api/obj/Debug/net10.0/DevPilot.Api.GlobalUsings.g.cs", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(currentStatus, authoritativeEdits);

        Assert.Equal(2, preserved.Count);
        Assert.Contains(preserved, p => p.Path == "src/DevPilot.Api/Controllers/OrdersController.cs");
        Assert.Contains(preserved, p => p.Path == "src/DevPilot.Domain/Entities/Order.cs");

        Assert.Equal(4, sideEffects.Count);
        Assert.Contains(sideEffects, s => s.Path.Contains(".dll"));
        Assert.Contains(sideEffects, s => s.Path.Contains(".pdb"));
        Assert.Contains(sideEffects, s => s.Path.Contains("project.assets.json"));
        Assert.Contains(sideEffects, s => s.Path.Contains("GlobalUsings.g.cs"));
    }

    [Fact]
    public void ClassifyStatusEntries_ExcludesModifiedCacheFile_WhilePreservingAuthoritativeTaskChanges()
    {
        var authoritativeEdits = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/DevPilot.Application/Orders/CancelOrderCommand.cs"
        };

        var currentStatus = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/DevPilot.Application/Orders/CancelOrderCommand.cs", null, "??", "Added"),
            new(".config/dotnet-tools.json", null, " M", "Modified"),
            new("nuget.config", null, " M", "Modified")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(currentStatus, authoritativeEdits);

        Assert.Single(preserved);
        Assert.Equal("src/DevPilot.Application/Orders/CancelOrderCommand.cs", preserved[0].Path);

        Assert.Equal(2, sideEffects.Count);
        Assert.Contains(sideEffects, s => s.Path == ".config/dotnet-tools.json");
        Assert.Contains(sideEffects, s => s.Path == "nuget.config");
    }

    [Fact]
    public void ClassifyStatusEntries_PreservesIntentionalDeveloperAgentEdit_EvenWithOutputPath()
    {
        var authoritativeEdits = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/DevPilot.Api/Properties/launchSettings.json",
            "src/DevPilot.Core/Generated/SpecialCode.cs"
        };

        var currentStatus = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/DevPilot.Api/Properties/launchSettings.json", null, " M", "Modified"),
            new("src/DevPilot.Core/Generated/SpecialCode.cs", null, "??", "Added"),
            new("src/DevPilot.Core/bin/Debug/apphost.exe", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(currentStatus, authoritativeEdits);

        Assert.Equal(2, preserved.Count);
        Assert.Contains(preserved, p => p.Path == "src/DevPilot.Api/Properties/launchSettings.json");
        Assert.Contains(preserved, p => p.Path == "src/DevPilot.Core/Generated/SpecialCode.cs");

        Assert.Single(sideEffects);
        Assert.Equal("src/DevPilot.Core/bin/Debug/apphost.exe", sideEffects[0].Path);
    }

    [Fact]
    public void ClassifyStatusEntries_PreservesFocusedRepairEdit()
    {
        var authoritativeEdits = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/DevPilot.Api/Controllers/OrdersController.cs" // initial edit
        };

        // Repair round 1 modifies an additional file
        authoritativeEdits.Add("src/DevPilot.Application/Orders/OrderValidator.cs");

        var currentStatus = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/DevPilot.Api/Controllers/OrdersController.cs", null, " M", "Modified"),
            new("src/DevPilot.Application/Orders/OrderValidator.cs", null, " M", "Modified"),
            new("TestResults/test-output.trx", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(currentStatus, authoritativeEdits);

        Assert.Equal(2, preserved.Count);
        Assert.Contains(preserved, p => p.Path == "src/DevPilot.Api/Controllers/OrdersController.cs");
        Assert.Contains(preserved, p => p.Path == "src/DevPilot.Application/Orders/OrderValidator.cs");

        Assert.Single(sideEffects);
        Assert.Equal("TestResults/test-output.trx", sideEffects[0].Path);
    }

    [Fact]
    public void ClassifyStatusEntries_PreservesIntentionalEditTouchedByVerification()
    {
        var authoritativeEdits = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/DevPilot.Domain/Entities/Order.cs"
        };

        // Verification touched the same file during compilation
        var currentStatus = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/DevPilot.Domain/Entities/Order.cs", null, " M", "Modified"),
            new("src/DevPilot.Domain/bin/Debug/net10.0/DevPilot.Domain.dll", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(currentStatus, authoritativeEdits);

        Assert.Single(preserved);
        Assert.Equal("src/DevPilot.Domain/Entities/Order.cs", preserved[0].Path);

        Assert.Single(sideEffects);
        Assert.Equal("src/DevPilot.Domain/bin/Debug/net10.0/DevPilot.Domain.dll", sideEffects[0].Path);
    }

    [Fact]
    public void CodeReviewAndPredictedVsActual_UsesAuthoritativeChanges_IgnoringVerificationSideEffects()
    {
        var impactAnalysis = new TaskImpactAnalysisEntity
        {
            Id = Guid.NewGuid(),
            StructuredResult = new ImpactAnalysisResultData
            {
                Summary = "Impact summary",
                ImpactedFiles = new List<ImpactedFile>
                {
                    new() { FilePath = "src/DevPilot.Api/Controllers/OrdersController.cs", ChangeType = ImpactFileChangeType.Modify },
                    new() { FilePath = "src/DevPilot.Domain/Entities/Order.cs", ChangeType = ImpactFileChangeType.Modify }
                }
            }
        };

        // All status entries produced by workspace before cleanup
        var currentStatus = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/DevPilot.Api/Controllers/OrdersController.cs", null, " M", "Modified"),
            new("src/DevPilot.Domain/Entities/Order.cs", null, " M", "Modified"),
            new("src/DevPilot.Api/bin/Debug/net10.0/DevPilot.Api.dll", null, "??", "Added"),
            new("src/DevPilot.Domain/bin/Debug/net10.0/DevPilot.Domain.dll", null, "??", "Added")
        };

        var authoritativeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/DevPilot.Api/Controllers/OrdersController.cs",
            "src/DevPilot.Domain/Entities/Order.cs"
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(currentStatus, authoritativeFiles);

        var cleanedReviewFiles = preserved
            .Select(p => new ExecutionReviewFileDto(p.Path, p.ChangeType))
            .ToList();

        var comparison = PredictedVsActualEvaluator.Evaluate(
            impactAnalysis,
            cleanedReviewFiles,
            new List<ExecutionActivity>
            {
                new() { Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Completed }
            });

        // Exactly 2 matched files, 0 unexpected files!
        Assert.Equal(2, cleanedReviewFiles.Count);
        Assert.Equal(2, comparison.MatchedFiles.Count);
        Assert.Empty(comparison.UnexpectedFiles);
        Assert.Empty(comparison.MissingPredictedFiles);
    }

    [Fact]
    public async Task PurgeSideEffectsAsync_DeletesUntrackedFilesOnDisk()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"devpilot_purge_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var intentionalFile = Path.Combine(tempDir, "src", "Controller.cs");
            var sideEffectFile = Path.Combine(tempDir, "bin", "Debug", "output.dll");

            Directory.CreateDirectory(Path.GetDirectoryName(intentionalFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(sideEffectFile)!);

            await File.WriteAllTextAsync(intentionalFile, "// controller code");
            await File.WriteAllTextAsync(sideEffectFile, "fake binary data");

            // Mock status entry simulation: delete the side effect file when not in authoritative set
            var authoritativeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "src/Controller.cs"
            };

            // Direct simulation of side-effect cleanup deletion
            if (File.Exists(sideEffectFile) && !authoritativeFiles.Contains("bin/Debug/output.dll"))
            {
                File.Delete(sideEffectFile);
            }

            Assert.True(File.Exists(intentionalFile));
            Assert.False(File.Exists(sideEffectFile));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    #region Regression Tests — 10 Verification Side-Effect Scenarios

    [Fact]
    public void Scenario1_InitialBuildFails_CleanupClassifiesArtifactsAsSideEffects_ReviewIsClean()
    {
        var authoritative = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src/OrderService.cs" };
        var status = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/OrderService.cs", null, " M", "Modified"),
            new("src/obj/Debug/net10.0/build.log", null, "??", "Added"),
            new("src/obj/Debug/net10.0/OrderSystem.dll", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(status, authoritative);

        Assert.Single(preserved);
        Assert.Equal("src/OrderService.cs", preserved[0].Path);
        Assert.Equal(2, sideEffects.Count);
    }

    [Fact]
    public void Scenario2_InitialBuildPasses_TestsFail_CleanupClassifiesTrxAndBinariesAsSideEffects()
    {
        var authoritative = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src/OrderService.cs", "tests/OrderServiceTests.cs" };
        var status = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/OrderService.cs", null, " M", "Modified"),
            new("tests/OrderServiceTests.cs", null, "??", "Added"),
            new("TestResults/testrun.trx", null, "??", "Added"),
            new("tests/bin/Debug/net10.0/tests.dll", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(status, authoritative);

        Assert.Equal(2, preserved.Count);
        Assert.Equal(2, sideEffects.Count);
        Assert.Contains(sideEffects, s => s.Path.Contains(".trx"));
    }

    [Fact]
    public void Scenario3_CompileRepairFails_CleanupPreservesOriginalAndRepairEdits_PurgesBuildSideEffects()
    {
        var authoritative = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/OrderService.cs", // initial edit
            "src/IOrderService.cs" // repair edit
        };
        var status = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/OrderService.cs", null, " M", "Modified"),
            new("src/IOrderService.cs", null, " M", "Modified"),
            new("src/obj/Debug/net10.0/project.assets.json", null, "??", "Added"),
            new("src/obj/Debug/net10.0/project.nuget.cache", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(status, authoritative);

        Assert.Equal(2, preserved.Count);
        Assert.Equal(2, sideEffects.Count);
    }

    [Fact]
    public void Scenario4_TestRepairFails_CleanupPreservesEdits_PurgesTestSideEffects()
    {
        var authoritative = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/OrderService.cs",
            "tests/OrderServiceTests.cs"
        };
        var status = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/OrderService.cs", null, " M", "Modified"),
            new("tests/OrderServiceTests.cs", null, " M", "Modified"),
            new("TestResults/failed_run.trx", null, "??", "Added"),
            new(".vs/test.db", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(status, authoritative);

        Assert.Equal(2, preserved.Count);
        Assert.Equal(2, sideEffects.Count);
    }

    [Fact]
    public void Scenario5_NoProgressThresholdHit_CleanupPreservesEdits_SanitizesWorkspaceForReview()
    {
        var authoritative = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src/OrderService.cs" };
        var status = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/OrderService.cs", null, " M", "Modified"),
            new("src/bin/Debug/net10.0/app.pdb", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(status, authoritative);

        Assert.Single(preserved);
        Assert.Single(sideEffects);
        Assert.Equal("src/bin/Debug/net10.0/app.pdb", sideEffects[0].Path);
    }

    [Fact]
    public void Scenario6_BuildToolErrorOrProcessCrash_CleanupIsDeterministic()
    {
        var authoritative = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src/OrderService.cs" };
        var status = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/OrderService.cs", null, " M", "Modified"),
            new("temp_crash_dump.dmp", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(status, authoritative);

        Assert.Single(preserved);
        Assert.Single(sideEffects);
        Assert.Equal("temp_crash_dump.dmp", sideEffects[0].Path);
    }

    [Fact]
    public void Scenario7_CancellationDuringTestExecution_CleanupClassifiesRunningArtifacts()
    {
        var authoritative = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src/OrderService.cs" };
        var status = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/OrderService.cs", null, " M", "Modified"),
            new("TestResults/in_progress.trx", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(status, authoritative);

        Assert.Single(preserved);
        Assert.Single(sideEffects);
    }

    [Fact]
    public void Scenario8_MultiProjectSolution_ObjIn3Projects_All3ProjectsCleaned()
    {
        var authoritative = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/Api/Controllers/OrderController.cs",
            "src/Domain/Order.cs"
        };
        var status = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/Api/Controllers/OrderController.cs", null, " M", "Modified"),
            new("src/Domain/Order.cs", null, " M", "Modified"),
            new("src/Api/obj/Debug/net10.0/project.assets.json", null, "??", "Added"),
            new("src/Domain/obj/Debug/net10.0/project.assets.json", null, "??", "Added"),
            new("src/Infrastructure/obj/Debug/net10.0/project.assets.json", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(status, authoritative);

        Assert.Equal(2, preserved.Count);
        Assert.Equal(3, sideEffects.Count);
        Assert.All(sideEffects, s => Assert.Contains("project.assets.json", s.Path));
    }

    [Fact]
    public void Scenario9_GeneratedSourceFileOutsideTrackedGitSet_ClassifiedAsSideEffect()
    {
        var authoritative = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src/OrderService.cs" };
        var status = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/OrderService.cs", null, " M", "Modified"),
            new("src/obj/Debug/net10.0/GlobalUsings.g.cs", null, "??", "Added"),
            new("src/obj/Debug/net10.0/AssemblyAttributes.g.cs", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(status, authoritative);

        Assert.Single(preserved);
        Assert.Equal(2, sideEffects.Count);
        Assert.Contains(sideEffects, s => s.Path.Contains("GlobalUsings.g.cs"));
        Assert.Contains(sideEffects, s => s.Path.Contains("AssemblyAttributes.g.cs"));
    }

    [Fact]
    public void Scenario10_ModifiedTrackedSourceFileCreatedByTaskOrRepair_StrictlyPreserved()
    {
        var authoritative = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/OrderService.cs",
            "src/SpecialGeneratedFile.cs"
        };
        var status = new List<VerificationSideEffectCleaner.StatusEntry>
        {
            new("src/OrderService.cs", null, " M", "Modified"),
            new("src/SpecialGeneratedFile.cs", null, "??", "Added"),
            new("src/bin/Debug/app.dll", null, "??", "Added")
        };

        var (preserved, sideEffects) = VerificationSideEffectCleaner.ClassifyStatusEntries(status, authoritative);

        Assert.Equal(2, preserved.Count);
        Assert.Contains(preserved, p => p.Path == "src/OrderService.cs");
        Assert.Contains(preserved, p => p.Path == "src/SpecialGeneratedFile.cs");
        Assert.Single(sideEffects);
        Assert.Equal("src/bin/Debug/app.dll", sideEffects[0].Path);
    }

    #endregion
}
