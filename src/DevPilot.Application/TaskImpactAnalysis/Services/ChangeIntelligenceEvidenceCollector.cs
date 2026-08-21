using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Models;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;

namespace DevPilot.Application.TaskImpactAnalysis.Services;

public sealed record RepositoryEvidenceProfile(
    IReadOnlyList<DiscoveredProjectNode>? ProjectGraph = null,
    IReadOnlyList<string>? ProjectRoots = null,
    RepositoryProfile? VerificationProfile = null,
    IReadOnlyList<string>? InventoryCsFiles = null,
    IReadOnlyList<string>? ControllerFiles = null,
    IReadOnlyList<string>? PersistenceFiles = null,
    IReadOnlyList<string>? MigrationFiles = null,
    IReadOnlyList<string>? TestFiles = null,
    bool HasEfCore = false,
    bool HasTestProjects = false)
{
    public IReadOnlyList<DiscoveredProjectNode> ProjectGraph { get; init; } = ProjectGraph ?? Array.Empty<DiscoveredProjectNode>();
    public IReadOnlyList<string> ProjectRoots { get; init; } = ProjectRoots ?? Array.Empty<string>();
    public RepositoryProfile VerificationProfile { get; init; } = VerificationProfile ?? new RepositoryProfile(RepositoryVerificationState.Unconfigured, Array.Empty<string>(), Array.Empty<RepositoryCheck>(), null);
    public IReadOnlyList<string> InventoryCsFiles { get; init; } = InventoryCsFiles ?? Array.Empty<string>();
    public IReadOnlyList<string> ControllerFiles { get; init; } = ControllerFiles ?? Array.Empty<string>();
    public IReadOnlyList<string> PersistenceFiles { get; init; } = PersistenceFiles ?? Array.Empty<string>();
    public IReadOnlyList<string> MigrationFiles { get; init; } = MigrationFiles ?? Array.Empty<string>();
    public IReadOnlyList<string> TestFiles { get; init; } = TestFiles ?? Array.Empty<string>();
    public bool HasEfCore { get; init; } = HasEfCore;
    public bool HasTestProjects { get; init; } = HasTestProjects;
}

public static class ChangeIntelligenceEvidenceCollector
{
    public static RepositoryEvidenceProfile CollectEvidence(
        string workspaceLocalPath,
        RepositoryProfile verificationProfile,
        RepositoryAnalysisResult? roslynResult = null)
    {
        var projectGraph = ProjectGraphHelper.DiscoverProjectGraph(workspaceLocalPath);
        var projectRoots = ProjectGraphHelper.DiscoverProjectRoots(workspaceLocalPath);

        var allCsFiles = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspaceLocalPath) && Directory.Exists(workspaceLocalPath))
        {
            try
            {
                var canonical = Path.GetFullPath(workspaceLocalPath);
                allCsFiles = ProjectGraphHelper.SafeFindFiles(canonical, "*.cs")
                    .Select(f => Path.GetRelativePath(canonical, f).Replace('\\', '/'))
                    .Where(f => !f.StartsWith("..", StringComparison.Ordinal))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                // Fallback to empty if error scanning
            }
        }

        var controllerFiles = allCsFiles
            .Where(f => f.Contains("Controller", StringComparison.OrdinalIgnoreCase) ||
                        f.Contains("/Controllers/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var persistenceFiles = allCsFiles
            .Where(f => f.Contains("DbContext", StringComparison.OrdinalIgnoreCase) ||
                        f.Contains("/Entities/", StringComparison.OrdinalIgnoreCase) ||
                        f.Contains("/Persistence/", StringComparison.OrdinalIgnoreCase) ||
                        f.Contains("/Data/", StringComparison.OrdinalIgnoreCase) ||
                        f.Contains("Configuration.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var migrationFiles = allCsFiles
            .Where(f => f.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase) ||
                        f.Contains("ModelSnapshot", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var testFiles = allCsFiles
            .Where(f => f.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase) ||
                        f.Contains("/Tests/", StringComparison.OrdinalIgnoreCase) ||
                        f.Contains(".Tests/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var hasEfCore = projectGraph.Any(p =>
            p.PackageReferences.Any(pkg => pkg.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)));

        var hasTestProjects = projectGraph.Any(p => p.IsTestProject) || testFiles.Count > 0;

        return new RepositoryEvidenceProfile(
            ProjectGraph: projectGraph,
            ProjectRoots: projectRoots,
            VerificationProfile: verificationProfile,
            InventoryCsFiles: allCsFiles,
            ControllerFiles: controllerFiles,
            PersistenceFiles: persistenceFiles,
            MigrationFiles: migrationFiles,
            TestFiles: testFiles,
            HasEfCore: hasEfCore,
            HasTestProjects: hasTestProjects);
    }

    public static bool IsHistoricalMigrationFile(string filePath, RepositoryEvidenceProfile evidence)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var norm = filePath.Replace('\\', '/');

        // ModelSnapshot is the cumulative schema snapshot, not an individual historical migration step
        if (norm.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // An existing file in repository inventory that belongs to Migrations folder or has migration designer extension
        if (evidence.MigrationFiles.Contains(norm, StringComparer.OrdinalIgnoreCase) ||
            (evidence.InventoryCsFiles.Contains(norm, StringComparer.OrdinalIgnoreCase) &&
             (norm.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase) ||
              norm.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        return false;
    }

    public static (string EvidenceType, string EvidenceDetails, bool IsUncertain) ClassifyFileEvidence(
        string normalizedPath,
        ImpactFileChangeType changeType,
        int? modelConfidence,
        RepositoryEvidenceProfile evidence)
    {
        var (evType, evDetails, isUncertain, _) = ClassifyAndCalibrateFileEvidence(
            normalizedPath,
            changeType,
            modelConfidence,
            evidence);

        return (evType, evDetails, isUncertain);
    }

    public static (string EvidenceType, string EvidenceDetails, bool IsUncertain, int CalibratedConfidence) ClassifyAndCalibrateFileEvidence(
        string normalizedPath,
        ImpactFileChangeType changeType,
        int? modelConfidence,
        RepositoryEvidenceProfile evidence)
    {
        var isInventoryMatch = evidence.InventoryCsFiles.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase) ||
                               evidence.ControllerFiles.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase) ||
                               evidence.PersistenceFiles.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase) ||
                               evidence.MigrationFiles.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase) ||
                               evidence.TestFiles.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase);

        string evType;
        string evDetails;
        bool isUncertain;

        // 1. Controller / API Surface
        if (evidence.ControllerFiles.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/Controllers/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.EndsWith("Controller.cs", StringComparison.OrdinalIgnoreCase))
        {
            isUncertain = !isInventoryMatch && changeType != ImpactFileChangeType.Add;
            evType = "ControllerUsage";
            evDetails = "Controller endpoint definition in API layer";
        }
        // 2. Migration
        else if (evidence.MigrationFiles.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase) ||
                 normalizedPath.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase))
        {
            isUncertain = !isInventoryMatch && changeType != ImpactFileChangeType.Add;
            evType = "MigrationRelationship";
            evDetails = "Database migration history or model snapshot";
        }
        // 3. Persistence / Entity
        else if (evidence.PersistenceFiles.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase) ||
                 normalizedPath.Contains("/Entities/", StringComparison.OrdinalIgnoreCase) ||
                 normalizedPath.Contains("DbContext", StringComparison.OrdinalIgnoreCase))
        {
            isUncertain = !isInventoryMatch && changeType != ImpactFileChangeType.Add;
            evType = "PersistenceRelationship";
            evDetails = "Entity, DbContext, or database configuration";
        }
        // 4. Test File
        else if (evidence.TestFiles.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase) ||
                 ProjectGraphHelper.IsTestFileCandidate(normalizedPath))
        {
            isUncertain = !isInventoryMatch && changeType != ImpactFileChangeType.Add;
            evType = "RelevantTest";
            evDetails = "Automated test suite component";
        }
        // 5. Interface
        else if (Path.GetFileName(normalizedPath).StartsWith("I", StringComparison.Ordinal) &&
                 Path.GetFileName(normalizedPath).Length > 2 &&
                 char.IsUpper(Path.GetFileName(normalizedPath)[1]))
        {
            isUncertain = !isInventoryMatch && changeType != ImpactFileChangeType.Add;
            evType = "InterfaceImplementation";
            evDetails = "Interface contract abstraction";
        }
        // 6. Existing Repository Inventory File
        else if (isInventoryMatch)
        {
            isUncertain = false;
            evType = "SymbolReference";
            evDetails = "Existing repository component match";
        }
        // 7. Newly Added File in Discovered Project Root
        else if (changeType == ImpactFileChangeType.Add)
        {
            isUncertain = false;
            evType = "Inferred";
            evDetails = "New component proposed for implementation";
        }
        // 8. Weak/Speculative
        else
        {
            isUncertain = true;
            evType = "Inferred";
            evDetails = "Speculative component reference";
        }

        var calibratedConfidence = CalibrateFileConfidence(modelConfidence, evType, isUncertain, changeType);

        return (evType, evDetails, isUncertain, calibratedConfidence);
    }

    public static int CalibrateFileConfidence(
        int? modelConfidence,
        string evidenceType,
        bool isUncertain,
        ImpactFileChangeType changeType)
    {
        // 1. Determine explainable deterministic baseline by evidence strength
        var baseline = evidenceType switch
        {
            "ControllerUsage" => 90,
            "MigrationRelationship" => 90,
            "PersistenceRelationship" => 90,
            "InterfaceImplementation" => 85,
            "RelevantTest" => 85,
            "SymbolReference" => 75,
            _ => changeType == ImpactFileChangeType.Add && !isUncertain ? 60 : 40
        };

        // 2. If model confidence is present and valid (1..100)
        if (modelConfidence.HasValue && modelConfidence.Value >= 1 && modelConfidence.Value <= 100)
        {
            var clampedModel = Math.Clamp(modelConfidence.Value, 1, 100);

            // If grounding proved the reference is uncertain or weak inferred, cap model confidence to lower range
            if (isUncertain || evidenceType == "Inferred")
            {
                return Math.Min(clampedModel, changeType == ImpactFileChangeType.Add ? 60 : 40);
            }

            return clampedModel;
        }

        // 3. If model confidence is absent (null/0) or out-of-range, use deterministic baseline
        return Math.Clamp(baseline, 0, 100);
    }

    public static ChangeBrief BuildChangeBrief(
        IReadOnlyList<ImpactedFile> impactedFiles,
        IReadOnlyList<Risk> risks,
        IReadOnlyList<SystemImpact> systemImpacts,
        IReadOnlyList<ChangeDimensionImpact> dimensions,
        IReadOnlyList<string> unknowns,
        RepositoryEvidenceProfile evidence,
        DatabaseImpact? databaseImpact = null)
    {
        var distinctProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in impactedFiles)
        {
            var root = evidence.ProjectRoots.FirstOrDefault(r =>
                !string.IsNullOrEmpty(r) && file.FilePath.StartsWith(r.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(root))
            {
                distinctProjects.Add(root);
            }
            else
            {
                var segments = file.FilePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 1) distinctProjects.Add(segments[0] + "/" + segments[1]);
            }
        }

        var riskLevel = EvaluateOverallRiskLevel(impactedFiles, risks, dimensions, evidence, databaseImpact);
        var riskReasons = BuildRiskReasons(impactedFiles, distinctProjects.Count, risks, dimensions, evidence, databaseImpact);

        var apiDim = dimensions.FirstOrDefault(d => string.Equals(d.Area, ChangeDimensionArea.Api, StringComparison.OrdinalIgnoreCase));
        var dataDim = dimensions.FirstOrDefault(d => string.Equals(d.Area, ChangeDimensionArea.Data, StringComparison.OrdinalIgnoreCase));
        var runtimeDim = dimensions.FirstOrDefault(d => string.Equals(d.Area, ChangeDimensionArea.Runtime, StringComparison.OrdinalIgnoreCase));
        var testsDim = dimensions.FirstOrDefault(d => string.Equals(d.Area, ChangeDimensionArea.Tests, StringComparison.OrdinalIgnoreCase));

        var expectedChecks = evidence.VerificationProfile.Checks
            .Select(c => new ExpectedVerificationCheck
            {
                CheckId = c.Id,
                DisplayName = c.DisplayName,
                Kind = c.Kind.ToString(),
                Required = c.Required,
                Source = c.Source.ToString(),
                DiscoveryEvidence = c.DiscoveryEvidence
            })
            .ToList();

        string verificationSummary;
        if (evidence.VerificationProfile.State == RepositoryVerificationState.Unconfigured)
        {
            verificationSummary = "Verification unconfigured: no trustworthy repository checks discovered";
        }
        else if (expectedChecks.Count > 0)
        {
            var checkNames = string.Join(", ", expectedChecks.Select(c => c.DisplayName));
            verificationSummary = $"{expectedChecks.Count} check(s) configured: {checkNames}";
        }
        else
        {
            verificationSummary = "No verification checks discovered";
        }

        var apiSummary = apiDim?.Summary ??
            (impactedFiles.Any(f => f.EvidenceType == "ControllerUsage") ? "API surface modified: controller endpoint affected" : null);

        var dataSummary = databaseImpact?.Summary ?? dataDim?.Summary ??
            (evidence.HasEfCore && impactedFiles.Any(f => f.EvidenceType is "PersistenceRelationship" or "MigrationRelationship")
                ? "Database schema/entity impacted; migration likely/expected"
                : impactedFiles.Any(f => f.EvidenceType is "PersistenceRelationship" or "MigrationRelationship")
                    ? "Persistence entity or configuration modified"
                    : null);

        var runtimeSummary = runtimeDim?.Summary;

        var testsSummary = testsDim?.Summary ??
            (!evidence.HasTestProjects
                ? "Missing test coverage: no automated test project discovered in repository"
                : impactedFiles.Any(f => f.EvidenceType == "RelevantTest")
                    ? "Test suite updated with relevant test coverage"
                    : null);

        return new ChangeBrief
        {
            FileCount = impactedFiles.Count,
            ProjectCount = distinctProjects.Count,
            RiskLevel = riskLevel,
            RiskReasons = riskReasons,
            ApiSummary = apiSummary,
            DataSummary = dataSummary,
            RuntimeSummary = runtimeSummary,
            TestsSummary = testsSummary,
            VerificationSummary = verificationSummary,
            ExpectedChecks = expectedChecks,
            DatabaseImpact = databaseImpact,
            Unknowns = unknowns.ToList()
        };
    }

    public static List<string> BuildRiskReasons(
        IReadOnlyList<ImpactedFile> files,
        int projectCount,
        IReadOnlyList<Risk> risks,
        IReadOnlyList<ChangeDimensionImpact> dimensions,
        RepositoryEvidenceProfile evidence,
        DatabaseImpact? databaseImpact = null)
    {
        var reasons = new List<string>();

        if (projectCount > 2)
        {
            reasons.Add($"Cross-project change affecting {projectCount} projects");
        }

        if (files.Count > 6)
        {
            reasons.Add($"Large change scope across {files.Count} files");
        }

        if (dimensions.Any(d => string.Equals(d.Area, ChangeDimensionArea.Api, StringComparison.OrdinalIgnoreCase)) ||
            files.Any(f => f.EvidenceType == "ControllerUsage"))
        {
            reasons.Add("API surface modified: controller / endpoint changes");
        }

        if (databaseImpact != null && databaseImpact.Changes.Count > 0)
        {
            if (databaseImpact.DataRiskLevel >= RiskLevel.High)
            {
                if (databaseImpact.RequiresDataMigration)
                {
                    reasons.Add("Persistent schema changes require data migration / backfill review for existing rows");
                }
                else
                {
                    reasons.Add("High-risk or destructive database schema modification detected");
                }
            }
            else
            {
                reasons.Add("Database schema or persistence entity impacted");
            }
        }
        else if (dimensions.Any(d => string.Equals(d.Area, ChangeDimensionArea.Data, StringComparison.OrdinalIgnoreCase)) ||
            files.Any(f => f.EvidenceType is "PersistenceRelationship" or "MigrationRelationship"))
        {
            reasons.Add("Database schema or persistence entity impacted");
        }

        if (dimensions.Any(d => string.Equals(d.Area, ChangeDimensionArea.Runtime, StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add("Touches concurrency, transaction, or runtime-sensitive code");
        }

        if (evidence.VerificationProfile.State == RepositoryVerificationState.Unconfigured)
        {
            reasons.Add("Repository verification is unconfigured");
        }
        else if (!evidence.HasTestProjects)
        {
            reasons.Add("No automated test suite discovered for verification");
        }

        var uncertainCount = files.Count(f => f.IsUncertain);
        if (uncertainCount > 0)
        {
            reasons.Add($"{uncertainCount} file(s) marked uncertain due to speculative evidence");
        }

        foreach (var r in risks.Where(r => r.Level >= RiskLevel.High).Take(2))
        {
            if (!reasons.Any(existing => existing.Contains(r.Description, StringComparison.OrdinalIgnoreCase)))
            {
                reasons.Add(r.Description);
            }
        }

        if (reasons.Count == 0)
        {
            reasons.Add("Standard isolated changes with low system blast radius");
        }

        return reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static RiskLevel EvaluateOverallRiskLevel(
        IReadOnlyList<ImpactedFile> files,
        IReadOnlyList<Risk> risks,
        IReadOnlyList<ChangeDimensionImpact> dimensions,
        RepositoryEvidenceProfile evidence,
        DatabaseImpact? databaseImpact = null)
    {
        if (risks.Any(r => r.Level == RiskLevel.Critical) ||
            dimensions.Any(d => d.ImpactLevel == SystemImpactLevel.Critical))
        {
            return RiskLevel.Critical;
        }

        if (databaseImpact != null && databaseImpact.DataRiskLevel >= RiskLevel.High)
        {
            return RiskLevel.High;
        }

        if (risks.Any(r => r.Level == RiskLevel.High) ||
            dimensions.Any(d => d.ImpactLevel == SystemImpactLevel.High) ||
            (dimensions.Any(d => string.Equals(d.Area, ChangeDimensionArea.Data, StringComparison.OrdinalIgnoreCase)) &&
             dimensions.Any(d => string.Equals(d.Area, ChangeDimensionArea.Api, StringComparison.OrdinalIgnoreCase))))
        {
            return RiskLevel.High;
        }

        if (files.Count >= 5 ||
            dimensions.Any(d => d.ImpactLevel == SystemImpactLevel.Medium) ||
            (databaseImpact != null && databaseImpact.DataRiskLevel == RiskLevel.Medium) ||
            evidence.VerificationProfile.State == RepositoryVerificationState.Unconfigured)
        {
            return RiskLevel.Medium;
        }

        return RiskLevel.Low;
    }

    public static List<string> SynthesizeUnknowns(
        IReadOnlyList<string>? rawUnknowns,
        IReadOnlyList<ImpactedFile> files,
        IReadOnlyList<ChangeDimensionImpact> dimensions,
        RepositoryEvidenceProfile evidence)
    {
        var unknowns = new List<string>();

        if (rawUnknowns != null)
        {
            foreach (var u in rawUnknowns.Where(u => !string.IsNullOrWhiteSpace(u)))
            {
                unknowns.Add(u.Trim());
            }
        }

        // Deterministic repository unknowns
        if (evidence.VerificationProfile.State == RepositoryVerificationState.Unconfigured)
        {
            const string unconfiguredMsg = "Repository verification checks are unconfigured in this repository";
            if (!unknowns.Contains(unconfiguredMsg, StringComparer.OrdinalIgnoreCase))
            {
                unknowns.Add(unconfiguredMsg);
            }
        }

        if (!evidence.HasTestProjects)
        {
            const string noTestsMsg = "No automated test project discovered in repository";
            if (!unknowns.Contains(noTestsMsg, StringComparer.OrdinalIgnoreCase))
            {
                unknowns.Add(noTestsMsg);
            }
        }

        var hasDataImpact = dimensions.Any(d => string.Equals(d.Area, ChangeDimensionArea.Data, StringComparison.OrdinalIgnoreCase)) ||
                            files.Any(f => f.EvidenceType == "PersistenceRelationship" || f.EvidenceType == "MigrationRelationship");

        if (hasDataImpact && evidence.HasEfCore)
        {
            const string rollbackMsg = "Migration rollback strategy not represented in repository evidence";
            if (!unknowns.Contains(rollbackMsg, StringComparer.OrdinalIgnoreCase))
            {
                unknowns.Add(rollbackMsg);
            }
        }

        return unknowns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
