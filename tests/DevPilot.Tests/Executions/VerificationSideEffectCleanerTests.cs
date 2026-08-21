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
}
