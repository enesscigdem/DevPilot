using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Discovers repository-owned verification commands from deterministic manifests and executes
/// them directly, without a shell, inside the verified execution worktree.
/// </summary>
/// <remarks>
/// Repository build manifests and package scripts can execute repository code and are not a
/// sandbox. This preserves the existing trusted-repository boundary; untrusted repositories
/// still require external process isolation.
/// </remarks>
public sealed class RepositoryNativeCheckRunner : IRepositoryCheckRunner
{
    public static readonly TimeSpan DefaultBuildTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultTestTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxAllowedTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MinAllowedTimeout = TimeSpan.FromSeconds(1);

    private const int MaxManifestBytes = 1_048_576;
    private const int MaxDiscoveredChecks = 50;

    private static readonly string[] ExcludedDirectoryNames =
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".dotnet_home", ".pnpm-store",
        "dist", "build", "coverage", "v0-reference"
    };

    private static readonly IReadOnlyDictionary<string, RepositoryCheckKind> NodeScriptKinds =
        new Dictionary<string, RepositoryCheckKind>(StringComparer.Ordinal)
        {
            ["build"] = RepositoryCheckKind.Build,
            ["typecheck"] = RepositoryCheckKind.TypeCheck,
            ["lint"] = RepositoryCheckKind.Lint,
            ["test"] = RepositoryCheckKind.Test
        };

    private readonly IExecutionWorkspaceManager _workspaceManager;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<RepositoryNativeCheckRunner> _logger;

    public RepositoryNativeCheckRunner(
        IExecutionWorkspaceManager workspaceManager,
        IProcessRunner processRunner,
        ILogger<RepositoryNativeCheckRunner> logger)
    {
        _workspaceManager = workspaceManager ?? throw new ArgumentNullException(nameof(workspaceManager));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RepositoryProfile> DiscoverAsync(
        RepositoryPreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ValidateWorkspaceAsync(
            request.WorkspacePath,
            request.BranchName,
            cancellationToken).ConfigureAwait(false);

        if (!workspace.IsValid)
        {
            return new RepositoryProfile(
                RepositoryVerificationState.InfrastructureFailure,
                Array.Empty<string>(),
                Array.Empty<RepositoryCheck>(),
                workspace.ErrorMessage);
        }

        try
        {
            var ecosystems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var checks = new List<RepositoryCheck>();
            var notes = new List<string>();

            DiscoverDotNetChecks(workspace.CanonicalWorkspace, ecosystems, checks, notes);
            DiscoverNodeChecks(workspace.CanonicalWorkspace, ecosystems, checks, notes);
            DiscoverPythonChecks(workspace.CanonicalWorkspace, ecosystems, checks, notes);

            var orderedChecks = checks
                .OrderBy(check => check.Order)
                .ThenBy(check => check.Id, StringComparer.Ordinal)
                .Take(MaxDiscoveredChecks)
                .ToList();

            if (checks.Count > MaxDiscoveredChecks)
            {
                notes.Add($"Repository verification discovery was limited to {MaxDiscoveredChecks} deterministic checks.");
            }

            if (orderedChecks.Count == 0)
            {
                var reason = notes.Count > 0
                    ? string.Join(" ", notes.Distinct(StringComparer.Ordinal).Take(5))
                    : "No trustworthy repository verification check could be determined from supported manifests.";

                return new RepositoryProfile(
                    RepositoryVerificationState.Unconfigured,
                    ecosystems.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                    orderedChecks,
                    reason,
                    HasUnresolvedVerification: true);
            }

            return new RepositoryProfile(
                RepositoryVerificationState.Configured,
                ecosystems.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                orderedChecks,
                notes.Count == 0 ? null : string.Join(" ", notes.Distinct(StringComparer.Ordinal).Take(5)),
                HasUnresolvedVerification: notes.Count > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Repository verification discovery failed for workspace '{WorkspacePath}'.", request.WorkspacePath);
            return new RepositoryProfile(
                RepositoryVerificationState.InfrastructureFailure,
                Array.Empty<string>(),
                Array.Empty<RepositoryCheck>(),
                $"Repository verification discovery failed: {ex.Message}");
        }
    }

    public async Task<RepositoryCheckResult> ExecuteAsync(
        RepositoryCheckExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Check == null)
        {
            return InfrastructureFailure(string.Empty, string.Empty, RepositoryCheckKind.Other, "Repository check cannot be null.");
        }

        var check = request.Check;
        var workspace = await ValidateWorkspaceAsync(
            request.WorkspacePath,
            request.BranchName,
            cancellationToken).ConfigureAwait(false);

        if (!workspace.IsValid)
        {
            return InfrastructureFailure(check.Id, check.DisplayName, check.Kind, workspace.ErrorMessage!);
        }

        if (check.Timeout < MinAllowedTimeout || check.Timeout > MaxAllowedTimeout)
        {
            return InfrastructureFailure(
                check.Id,
                check.DisplayName,
                check.Kind,
                $"Repository check timeout must be between {MinAllowedTimeout.TotalSeconds} seconds and {MaxAllowedTimeout.TotalMinutes} minutes.");
        }

        if (!TryResolveWorkingDirectory(workspace.CanonicalWorkspace, check.WorkingDirectory, out var workingDirectory, out var pathError))
        {
            return InfrastructureFailure(check.Id, check.DisplayName, check.Kind, pathError!);
        }

        bool approvedCommand;
        string? commandError;
        try
        {
            approvedCommand = TryValidateApprovedCommand(workspace.CanonicalWorkspace, check, out commandError);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return InfrastructureFailure(check.Id, check.DisplayName, check.Kind, $"Repository check evidence could not be validated: {ex.Message}");
        }

        if (!approvedCommand)
        {
            return InfrastructureFailure(check.Id, check.DisplayName, check.Kind, commandError!);
        }

        var arguments = check.Arguments.ToList();
        if (request.SkipBuild)
        {
            if (!check.SupportsSkipBuild)
            {
                return InfrastructureFailure(check.Id, check.DisplayName, check.Kind, $"Repository check '{check.Id}' does not support skipping its build prerequisite.");
            }

            arguments.Add("--no-build");
        }

        if (!string.IsNullOrWhiteSpace(request.TestFilter))
        {
            if (!check.SupportsTargetedTest ||
                !Regex.IsMatch(request.TestFilter, @"^[A-Za-z_][A-Za-z0-9_.+`]*$", RegexOptions.CultureInvariant))
            {
                return InfrastructureFailure(check.Id, check.DisplayName, check.Kind, $"Repository check '{check.Id}' does not support the requested targeted test filter.");
            }

            arguments.Add("--filter");
            arguments.Add($"FullyQualifiedName={request.TestFilter}");
        }

        _logger.LogInformation(
            "Executing repository check {CheckId} ({CheckKind}) in '{WorkingDirectory}'.",
            check.Id,
            check.Kind,
            workingDirectory);

        var processResult = await _processRunner.RunProcessAsync(
            check.Executable,
            arguments,
            workingDirectory,
            check.Timeout,
            cancellationToken).ConfigureAwait(false);

        var infrastructureFailure = processResult.IsTimedOut ||
                                    processResult.ExitCode < 0 ||
                                    !string.IsNullOrWhiteSpace(processResult.ErrorMessage) ||
                                    IsMissingRuntimeDependency(processResult);
        var success = !infrastructureFailure && processResult.ExitCode == 0;
        var failureCategory = success
            ? RepositoryCheckFailureCategory.None
            : infrastructureFailure
                ? RepositoryCheckFailureCategory.InfrastructureFailure
                : RepositoryCheckFailureCategory.VerificationFailure;

        return new RepositoryCheckResult
        {
            CheckId = check.Id,
            CheckDisplayName = check.DisplayName,
            CheckKind = check.Kind,
            FailureCategory = failureCategory,
            Success = success,
            ExitCode = processResult.ExitCode,
            ErrorMessage = success
                ? null
                : processResult.ErrorMessage ?? $"{check.DisplayName} failed with exit code {processResult.ExitCode}.",
            StartTime = processResult.StartTime,
            CompletionTime = processResult.CompletionTime,
            Duration = processResult.Duration,
            StdOut = processResult.StdOut,
            StdErr = processResult.StdErr,
            IsTruncated = processResult.IsTruncated,
            IsTimedOut = processResult.IsTimedOut,
            TargetPath = check.EvidencePath
        };
    }

    private static void DiscoverDotNetChecks(
        string workspace,
        ISet<string> ecosystems,
        ICollection<RepositoryCheck> checks,
        ICollection<string> notes)
    {
        var rootSolutions = Directory.GetFiles(workspace, "*.sln", SearchOption.TopDirectoryOnly)
            .Select(GetCanonicalRealPath)
            .Where(path => IsSubPath(workspace, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var allSolutions = SafeFindFiles(workspace, "*.sln");
        var allProjects = SafeFindFiles(workspace, "*.csproj");

        if (rootSolutions.Count == 0 && allSolutions.Count == 0 && allProjects.Count == 0)
        {
            return;
        }

        ecosystems.Add("dotnet");

        string? buildTarget = null;
        string? buildDiscoveryEvidence = null;
        var buildTargetFromProjectGraph = false;
        if (rootSolutions.Count == 1)
        {
            buildTarget = rootSolutions[0];
            buildDiscoveryEvidence = "Single solution file at the repository root.";
        }
        else if (rootSolutions.Count > 1)
        {
            notes.Add(".NET build verification is ambiguous because multiple root solution files exist.");
        }
        else if (allSolutions.Count == 1)
        {
            buildTarget = allSolutions[0];
            buildDiscoveryEvidence = "Single solution file in the repository.";
        }
        else if (allSolutions.Count > 1)
        {
            notes.Add(".NET build verification is ambiguous because multiple solution files exist.");
        }
        else if (allProjects.Count == 1)
        {
            buildTarget = allProjects[0];
            buildDiscoveryEvidence = "Single project file in the repository.";
        }
        else if (allProjects.Count > 1)
        {
            if (TryResolveUniqueProjectReferenceRoot(
                    workspace,
                    allProjects,
                    out var projectRoot,
                    out var projectGraphEvidence,
                    out var ambiguityReason))
            {
                buildTarget = projectRoot;
                buildDiscoveryEvidence = projectGraphEvidence;
                buildTargetFromProjectGraph = true;
            }
            else
            {
                notes.Add($".NET build verification is ambiguous because {ambiguityReason}");
            }
        }

        if (buildTarget != null)
        {
            var relativeTarget = NormalizeRelativePath(Path.GetRelativePath(workspace, buildTarget));
            checks.Add(new RepositoryCheck(
                $"dotnet:build:{relativeTarget}",
                ".NET build",
                RepositoryCheckKind.Build,
                "dotnet",
                "dotnet",
                new[] { "build", relativeTarget },
                ".",
                true,
                DefaultBuildTimeout,
                RepositoryCheckSource.DotNetManifest,
                relativeTarget,
                Order: 100,
                DiscoveryEvidence: buildDiscoveryEvidence));
        }

        if (buildTarget == null)
        {
            return;
        }

        var testProjects = allProjects
            .Where(path => Path.GetFileName(path).Contains("Test", StringComparison.OrdinalIgnoreCase))
            .ToList();
        string? testTarget = null;
        string? testDiscoveryEvidence = null;

        if (testProjects.Count == 1)
        {
            testTarget = testProjects[0];
            testDiscoveryEvidence = "Single test project identified from project metadata path evidence.";
        }
        else if (testProjects.Count > 1)
        {
            notes.Add(".NET test verification is ambiguous because multiple test projects exist.");
        }
        else if (rootSolutions.Count == 1)
        {
            testTarget = rootSolutions[0];
            testDiscoveryEvidence = "Single root solution used as the deterministic test target.";
        }
        else if (buildTargetFromProjectGraph)
        {
            testTarget = buildTarget;
            testDiscoveryEvidence = $"{buildDiscoveryEvidence} The same deterministic project root is used for dotnet test.";
        }

        if (testTarget != null)
        {
            var relativeTarget = NormalizeRelativePath(Path.GetRelativePath(workspace, testTarget));
            checks.Add(new RepositoryCheck(
                $"dotnet:test:{relativeTarget}",
                ".NET tests",
                RepositoryCheckKind.Test,
                "dotnet",
                "dotnet",
                new[] { "test", relativeTarget },
                ".",
                true,
                DefaultTestTimeout,
                RepositoryCheckSource.DotNetManifest,
                relativeTarget,
                SupportsSkipBuild: buildTarget != null,
                SupportsTargetedTest: true,
                Order: 400,
                DiscoveryEvidence: testDiscoveryEvidence));
        }
        else if (buildTarget != null && testProjects.Count == 0)
        {
            notes.Add("No deterministic .NET test target was found; only the discovered build check will run.");
        }
    }

    private static bool TryResolveUniqueProjectReferenceRoot(
        string workspace,
        IReadOnlyList<string> projects,
        out string? rootProject,
        out string? discoveryEvidence,
        out string ambiguityReason)
    {
        rootProject = null;
        discoveryEvidence = null;
        ambiguityReason = "no solution and multiple project files exist.";

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var projectSet = projects.ToHashSet(comparer);
        var references = projects.ToDictionary(project => project, _ => new HashSet<string>(comparer), comparer);

        foreach (var project in projects)
        {
            if (!TryReadProjectReferences(workspace, project, projectSet, references[project], out ambiguityReason))
            {
                return false;
            }
        }

        var incomingReferenceCount = projects.ToDictionary(project => project, _ => 0, comparer);
        foreach (var dependency in references.Values.SelectMany(projectReferences => projectReferences))
        {
            incomingReferenceCount[dependency]++;
        }

        var roots = projects
            .Where(project => incomingReferenceCount[project] == 0)
            .OrderBy(project => project, comparer)
            .ToList();

        if (!IsAcyclicProjectGraph(projects, references, incomingReferenceCount, comparer))
        {
            ambiguityReason = "the ProjectReference graph contains a cycle.";
            return false;
        }

        if (roots.Count != 1)
        {
            ambiguityReason = roots.Count == 0
                ? "the ProjectReference graph has no root project."
                : $"the ProjectReference graph has {roots.Count} independent root projects.";
            return false;
        }

        var reachable = new HashSet<string>(comparer);
        var pending = new Stack<string>();
        pending.Push(roots[0]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!reachable.Add(current))
            {
                continue;
            }

            foreach (var dependency in references[current])
            {
                pending.Push(dependency);
            }
        }

        if (reachable.Count != projects.Count)
        {
            ambiguityReason = "the unique ProjectReference root does not transitively reach every discovered project.";
            return false;
        }

        rootProject = roots[0];
        var relativeRoot = NormalizeRelativePath(Path.GetRelativePath(workspace, rootProject));
        discoveryEvidence = $"Selected '{relativeRoot}' as the unique acyclic ProjectReference root reaching all {projects.Count} projects.";
        return true;
    }

    private static bool TryReadProjectReferences(
        string workspace,
        string project,
        ISet<string> projectSet,
        ISet<string> references,
        out string ambiguityReason)
    {
        ambiguityReason = string.Empty;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var stream = File.OpenRead(project);
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader, LoadOptions.None);

            foreach (var element in document.Descendants().Where(node => node.Name.LocalName == "ProjectReference"))
            {
                var include = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    ambiguityReason = $"project '{NormalizeRelativePath(Path.GetRelativePath(workspace, project))}' contains an invalid ProjectReference.";
                    return false;
                }

                var repositoryPath = include
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                var candidate = GetCanonicalRealPath(Path.Combine(Path.GetDirectoryName(project)!, repositoryPath));
                if (!IsSubPath(workspace, candidate) ||
                    !File.Exists(candidate) ||
                    !Path.GetExtension(candidate).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                    !projectSet.Contains(candidate))
                {
                    ambiguityReason = $"project '{NormalizeRelativePath(Path.GetRelativePath(workspace, project))}' references a missing, external, or unsupported project.";
                    return false;
                }

                references.Add(candidate);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            ambiguityReason = $"project '{NormalizeRelativePath(Path.GetRelativePath(workspace, project))}' could not be parsed as deterministic MSBuild XML.";
            return false;
        }
    }

    private static bool IsAcyclicProjectGraph(
        IReadOnlyList<string> projects,
        IReadOnlyDictionary<string, HashSet<string>> references,
        IReadOnlyDictionary<string, int> incomingReferenceCount,
        IEqualityComparer<string> comparer)
    {
        var remainingIncoming = incomingReferenceCount.ToDictionary(pair => pair.Key, pair => pair.Value, comparer);
        var pending = new Queue<string>(projects.Where(project => remainingIncoming[project] == 0));
        var visited = 0;

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            visited++;
            foreach (var dependency in references[current])
            {
                remainingIncoming[dependency]--;
                if (remainingIncoming[dependency] == 0)
                {
                    pending.Enqueue(dependency);
                }
            }
        }

        return visited == projects.Count;
    }

    private static void DiscoverNodeChecks(
        string workspace,
        ISet<string> ecosystems,
        ICollection<RepositoryCheck> checks,
        ICollection<string> notes)
    {
        foreach (var packageJsonPath in SafeFindFiles(workspace, "package.json"))
        {
            ecosystems.Add("node");
            if (new FileInfo(packageJsonPath).Length > MaxManifestBytes)
            {
                notes.Add($"Skipped oversized package manifest '{NormalizeRelativePath(Path.GetRelativePath(workspace, packageJsonPath))}'.");
                continue;
            }

            if (!TryReadJsonDocument(packageJsonPath, out var parsedDocument))
            {
                notes.Add($"Package manifest '{NormalizeRelativePath(Path.GetRelativePath(workspace, packageJsonPath))}' is not valid JSON.");
                continue;
            }

            using var document = parsedDocument!;
            if (!document.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var packageDirectory = Path.GetDirectoryName(packageJsonPath)!;
            var manager = DetectPackageManager(document.RootElement, packageDirectory, out var managerError);
            if (manager == null)
            {
                notes.Add(managerError!);
                continue;
            }

            var relativeManifest = NormalizeRelativePath(Path.GetRelativePath(workspace, packageJsonPath));
            var relativeWorkingDirectory = NormalizeRelativePath(Path.GetRelativePath(workspace, packageDirectory));
            if (relativeWorkingDirectory.Length == 0)
            {
                relativeWorkingDirectory = ".";
            }

            var checkCountBeforeManifest = checks.Count;
            foreach (var mapping in NodeScriptKinds)
            {
                if (!scripts.TryGetProperty(mapping.Key, out var scriptValue) ||
                    scriptValue.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(scriptValue.GetString()))
                {
                    continue;
                }

                var fingerprint = ComputeFingerprint(manager, mapping.Key, scriptValue.GetString()!);
                checks.Add(new RepositoryCheck(
                    $"node:{relativeManifest}:{mapping.Key}",
                    $"{manager} {mapping.Key}",
                    mapping.Value,
                    "node",
                    manager,
                    new[] { "run", mapping.Key },
                    relativeWorkingDirectory,
                    true,
                    mapping.Value == RepositoryCheckKind.Test ? DefaultTestTimeout : DefaultBuildTimeout,
                    RepositoryCheckSource.PackageJsonScript,
                    relativeManifest,
                    fingerprint,
                    Order: GetOrder(mapping.Value, 10)));
            }

            if (checks.Count == checkCountBeforeManifest)
            {
                notes.Add($"No supported verification script is configured in '{relativeManifest}'.");
            }
        }
    }

    private static void DiscoverPythonChecks(
        string workspace,
        ISet<string> ecosystems,
        ICollection<RepositoryCheck> checks,
        ICollection<string> notes)
    {
        var pyprojectFiles = SafeFindFiles(workspace, "pyproject.toml");
        if (pyprojectFiles.Count == 0 && SafeFindFiles(workspace, "*.py").Count > 0)
        {
            ecosystems.Add("python");
            notes.Add("Python files were detected without an explicit supported verification configuration.");
        }

        foreach (var pyprojectPath in pyprojectFiles)
        {
            ecosystems.Add("python");
            if (new FileInfo(pyprojectPath).Length > MaxManifestBytes)
            {
                continue;
            }

            var content = File.ReadAllText(pyprojectPath);
            var checkCountBeforeManifest = checks.Count;
            var relativeManifest = NormalizeRelativePath(Path.GetRelativePath(workspace, pyprojectPath));
            var relativeWorkingDirectory = NormalizeRelativePath(Path.GetDirectoryName(relativeManifest) ?? string.Empty);
            if (relativeWorkingDirectory.Length == 0)
            {
                relativeWorkingDirectory = ".";
            }

            var fingerprint = ComputeFingerprint(content);
            if (HasTomlSection(content, "tool.pytest.ini_options"))
            {
                checks.Add(CreatePythonCheck(
                    relativeManifest,
                    relativeWorkingDirectory,
                    "pytest",
                    RepositoryCheckKind.Test,
                    new[] { "-m", "pytest" },
                    fingerprint,
                    420));
            }

            if (HasTomlSection(content, "tool.mypy"))
            {
                checks.Add(CreatePythonCheck(
                    relativeManifest,
                    relativeWorkingDirectory,
                    "mypy",
                    RepositoryCheckKind.TypeCheck,
                    new[] { "-m", "mypy", "." },
                    fingerprint,
                    220));
            }

            if (HasTomlSection(content, "tool.ruff") || HasTomlSection(content, "tool.ruff.lint"))
            {
                checks.Add(CreatePythonCheck(
                    relativeManifest,
                    relativeWorkingDirectory,
                    "ruff",
                    RepositoryCheckKind.Lint,
                    new[] { "-m", "ruff", "check", "." },
                    fingerprint,
                    320));
            }

            if (checks.Count == checkCountBeforeManifest)
            {
                notes.Add($"No supported Python verification tool is configured in '{relativeManifest}'.");
            }
        }
    }

    private static RepositoryCheck CreatePythonCheck(
        string manifest,
        string workingDirectory,
        string tool,
        RepositoryCheckKind kind,
        IReadOnlyList<string> arguments,
        string fingerprint,
        int order) =>
        new(
            $"python:{manifest}:{tool}",
            $"Python {tool}",
            kind,
            "python",
            "python",
            arguments,
            workingDirectory,
            true,
            kind == RepositoryCheckKind.Test ? DefaultTestTimeout : DefaultBuildTimeout,
            RepositoryCheckSource.PythonToolConfiguration,
            manifest,
            fingerprint,
            Order: order);

    private static bool TryValidateApprovedCommand(
        string workspace,
        RepositoryCheck check,
        out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(check.Id) || string.IsNullOrWhiteSpace(check.Executable))
        {
            errorMessage = "Repository check identity and executable are required.";
            return false;
        }

        if (!TryResolveEvidenceFile(workspace, check.EvidencePath, out var evidencePath, out errorMessage))
        {
            return false;
        }

        var expectedWorkingDirectory = check.Source == RepositoryCheckSource.DotNetManifest
            ? "."
            : NormalizeRelativePath(Path.GetRelativePath(workspace, Path.GetDirectoryName(evidencePath)!));
        if (expectedWorkingDirectory.Length == 0)
        {
            expectedWorkingDirectory = ".";
        }

        if (!string.Equals(
                NormalizeRelativePath(check.WorkingDirectory),
                expectedWorkingDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            errorMessage = $"Repository check '{check.Id}' working directory does not match its deterministic evidence location.";
            return false;
        }

        return check.Source switch
        {
            RepositoryCheckSource.DotNetManifest => ValidateDotNetCommand(check, evidencePath, out errorMessage),
            RepositoryCheckSource.PackageJsonScript => ValidateNodeCommand(check, evidencePath, out errorMessage),
            RepositoryCheckSource.PythonToolConfiguration => ValidatePythonCommand(check, evidencePath, out errorMessage),
            _ => RejectUnsupportedSource(out errorMessage)
        };
    }

    private static bool ValidateDotNetCommand(RepositoryCheck check, string evidencePath, out string? errorMessage)
    {
        errorMessage = null;
        var extension = Path.GetExtension(evidencePath);
        var expectedVerb = check.Kind switch
        {
            RepositoryCheckKind.Build => "build",
            RepositoryCheckKind.Test => "test",
            _ => null
        };

        if (!string.Equals(check.Ecosystem, "dotnet", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(check.Executable, "dotnet", StringComparison.Ordinal) ||
            expectedVerb == null ||
            check.Arguments.Count != 2 ||
            !string.Equals(check.Arguments[0], expectedVerb, StringComparison.Ordinal) ||
            !string.Equals(NormalizeRelativePath(check.Arguments[1]), NormalizeRelativePath(check.EvidencePath), StringComparison.Ordinal) ||
            !(extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            errorMessage = $"Repository check '{check.Id}' is not an approved .NET manifest command.";
            return false;
        }

        return true;
    }

    private static bool ValidateNodeCommand(RepositoryCheck check, string evidencePath, out string? errorMessage)
    {
        errorMessage = null;
        if (!string.Equals(check.Ecosystem, "node", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(evidencePath).Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
            new FileInfo(evidencePath).Length > MaxManifestBytes)
        {
            errorMessage = $"Repository check '{check.Id}' does not reference an approved package manifest.";
            return false;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(evidencePath));
        var manager = DetectPackageManager(document.RootElement, Path.GetDirectoryName(evidencePath)!, out var managerError);
        var expectedScript = NodeScriptKinds.FirstOrDefault(pair => pair.Value == check.Kind).Key;

        if (manager == null ||
            string.IsNullOrEmpty(expectedScript) ||
            !string.Equals(check.Executable, manager, StringComparison.Ordinal) ||
            check.Arguments.Count != 2 ||
            !string.Equals(check.Arguments[0], "run", StringComparison.Ordinal) ||
            !string.Equals(check.Arguments[1], expectedScript, StringComparison.Ordinal) ||
            !document.RootElement.TryGetProperty("scripts", out var scripts) ||
            scripts.ValueKind != JsonValueKind.Object ||
            !scripts.TryGetProperty(expectedScript, out var scriptValue) ||
            scriptValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(scriptValue.GetString()))
        {
            errorMessage = managerError ?? $"Repository check '{check.Id}' is not an approved package.json script command.";
            return false;
        }

        var currentFingerprint = ComputeFingerprint(manager, expectedScript, scriptValue.GetString()!);
        if (string.IsNullOrWhiteSpace(check.EvidenceFingerprint) ||
            !string.Equals(currentFingerprint, check.EvidenceFingerprint, StringComparison.Ordinal))
        {
            errorMessage = $"Repository-owned script evidence changed after preflight for check '{check.Id}'; refusing to execute a changed command.";
            return false;
        }

        return true;
    }

    private static bool ValidatePythonCommand(RepositoryCheck check, string evidencePath, out string? errorMessage)
    {
        errorMessage = null;
        if (!string.Equals(check.Ecosystem, "python", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(evidencePath).Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
            new FileInfo(evidencePath).Length > MaxManifestBytes ||
            !string.Equals(check.Executable, "python", StringComparison.Ordinal))
        {
            errorMessage = $"Repository check '{check.Id}' is not an approved Python configuration command.";
            return false;
        }

        var content = File.ReadAllText(evidencePath);
        var currentFingerprint = ComputeFingerprint(content);
        var expectedArguments = check.Kind switch
        {
            RepositoryCheckKind.Test when HasTomlSection(content, "tool.pytest.ini_options") => new[] { "-m", "pytest" },
            RepositoryCheckKind.TypeCheck when HasTomlSection(content, "tool.mypy") => new[] { "-m", "mypy", "." },
            RepositoryCheckKind.Lint when HasTomlSection(content, "tool.ruff") || HasTomlSection(content, "tool.ruff.lint") => new[] { "-m", "ruff", "check", "." },
            _ => Array.Empty<string>()
        };

        if (expectedArguments.Length == 0 ||
            !check.Arguments.SequenceEqual(expectedArguments, StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(check.EvidenceFingerprint) ||
            !string.Equals(currentFingerprint, check.EvidenceFingerprint, StringComparison.Ordinal))
        {
            errorMessage = $"Repository-owned Python verification evidence changed or is unsupported for check '{check.Id}'.";
            return false;
        }

        return true;
    }

    private static bool RejectUnsupportedSource(out string? errorMessage)
    {
        errorMessage = "Repository check source is not supported by deterministic discovery.";
        return false;
    }

    private async Task<(bool IsValid, string? ErrorMessage, string CanonicalWorkspace)> ValidateWorkspaceAsync(
        string workspacePath,
        string branchName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return (false, "Workspace path cannot be empty.", string.Empty);
        }

        if (string.IsNullOrWhiteSpace(branchName))
        {
            return (false, "Branch name cannot be empty.", string.Empty);
        }

        var verification = await _workspaceManager.VerifyWorkspaceStateAsync(
            workspacePath,
            branchName,
            requireClean: false,
            cancellationToken).ConfigureAwait(false);

        if (!verification.WorkspaceExists || !verification.BranchMatches || !verification.IsValid)
        {
            return (false, verification.ErrorMessage ?? "Execution workspace verification failed.", string.Empty);
        }

        return (true, null, GetCanonicalRealPath(workspacePath));
    }

    private static bool TryResolveWorkingDirectory(
        string workspace,
        string relativePath,
        out string workingDirectory,
        out string? errorMessage)
    {
        workingDirectory = string.Empty;
        errorMessage = null;
        if (!TryNormalizeRepositoryPath(relativePath, allowDot: true, out var normalized, out errorMessage))
        {
            return false;
        }

        var combined = normalized == "."
            ? workspace
            : Path.Combine(workspace, normalized.Replace('/', Path.DirectorySeparatorChar));
        var canonical = GetCanonicalRealPath(combined);
        if (!Directory.Exists(canonical) || !IsSubPath(workspace, canonical))
        {
            errorMessage = $"Repository check working directory '{relativePath}' is outside or missing from the execution workspace.";
            return false;
        }

        workingDirectory = canonical;
        return true;
    }

    private static bool TryResolveEvidenceFile(
        string workspace,
        string relativePath,
        out string evidencePath,
        out string? errorMessage)
    {
        evidencePath = string.Empty;
        if (!TryNormalizeRepositoryPath(relativePath, allowDot: false, out var normalized, out errorMessage))
        {
            return false;
        }

        var combined = Path.Combine(workspace, normalized.Replace('/', Path.DirectorySeparatorChar));
        var canonical = GetCanonicalRealPath(combined);
        if (!File.Exists(canonical) || !IsSubPath(workspace, canonical))
        {
            errorMessage = $"Repository check evidence '{relativePath}' is outside or missing from the execution workspace.";
            return false;
        }

        evidencePath = canonical;
        return true;
    }

    private static bool TryNormalizeRepositoryPath(
        string path,
        bool allowDot,
        out string normalized,
        out string? errorMessage)
    {
        normalized = string.Empty;
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathRooted(path) ||
            path.StartsWith('/') ||
            path.StartsWith('\\') ||
            (path.Length > 1 && path[1] == ':'))
        {
            errorMessage = $"Repository check path '{path}' must be a relative worktree path.";
            return false;
        }

        normalized = NormalizeRelativePath(path);
        if (allowDot && normalized == ".")
        {
            return true;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment == ".." || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            errorMessage = $"Repository check path '{path}' contains a forbidden path segment.";
            return false;
        }

        return true;
    }

    private static string? DetectPackageManager(JsonElement packageJson, string packageDirectory, out string? errorMessage)
    {
        errorMessage = null;
        if (packageJson.TryGetProperty("packageManager", out var packageManagerValue) && packageManagerValue.ValueKind == JsonValueKind.String)
        {
            var declared = packageManagerValue.GetString()?.Split('@', 2, StringSplitOptions.TrimEntries)[0];
            if (declared is "npm" or "pnpm" or "yarn" or "bun")
            {
                return declared;
            }

            errorMessage = $"Unsupported package manager declaration in '{Path.Combine(packageDirectory, "package.json")}'.";
            return null;
        }

        var detected = new List<string>();
        if (File.Exists(Path.Combine(packageDirectory, "package-lock.json")) || File.Exists(Path.Combine(packageDirectory, "npm-shrinkwrap.json"))) detected.Add("npm");
        if (File.Exists(Path.Combine(packageDirectory, "pnpm-lock.yaml"))) detected.Add("pnpm");
        if (File.Exists(Path.Combine(packageDirectory, "yarn.lock"))) detected.Add("yarn");
        if (File.Exists(Path.Combine(packageDirectory, "bun.lock")) || File.Exists(Path.Combine(packageDirectory, "bun.lockb"))) detected.Add("bun");

        var distinct = detected.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1)
        {
            errorMessage = $"Package manager evidence is ambiguous for '{Path.Combine(packageDirectory, "package.json")}'.";
            return null;
        }

        return distinct.Count == 1 ? distinct[0] : "npm";
    }

    private static bool HasTomlSection(string content, string section) =>
        Regex.IsMatch(
            content,
            $@"(?m)^\s*\[{Regex.Escape(section)}\]\s*(?:#.*)?$",
            RegexOptions.CultureInvariant);

    private static bool TryReadJsonDocument(string path, out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    private static int GetOrder(RepositoryCheckKind kind, int offset) => kind switch
    {
        RepositoryCheckKind.Build => 100 + offset,
        RepositoryCheckKind.TypeCheck => 200 + offset,
        RepositoryCheckKind.Lint => 300 + offset,
        RepositoryCheckKind.Test => 400 + offset,
        _ => 500 + offset
    };

    private static bool IsMissingRuntimeDependency(ProcessExecutionResult result)
    {
        if (result.ExitCode is not (126 or 127))
        {
            return false;
        }

        var output = $"{result.StdOut}\n{result.StdErr}";
        return output.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("is not recognized", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("could not determine executable", StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeFingerprint(params string[] values)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", values));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static RepositoryCheckResult InfrastructureFailure(
        string checkId,
        string displayName,
        RepositoryCheckKind kind,
        string errorMessage) => new()
        {
            CheckId = checkId,
            CheckDisplayName = displayName,
            CheckKind = kind,
            FailureCategory = RepositoryCheckFailureCategory.InfrastructureFailure,
            Success = false,
            ErrorMessage = errorMessage
        };

    private static List<string> SafeFindFiles(string rootPath, string searchPattern)
    {
        var results = new List<string>();
        var canonicalRoot = GetCanonicalRealPath(rootPath);
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        void Recurse(string currentDirectory)
        {
            var directoryName = Path.GetFileName(currentDirectory);
            if (ExcludedDirectoryNames.Any(excluded => excluded.Equals(directoryName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var canonicalDirectory = GetCanonicalRealPath(currentDirectory);
            if (!IsSubPath(canonicalRoot, canonicalDirectory) || !visited.Add(canonicalDirectory))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(currentDirectory, searchPattern).OrderBy(path => path, StringComparer.Ordinal))
            {
                var canonicalFile = GetCanonicalRealPath(file);
                if (IsSubPath(canonicalRoot, canonicalFile))
                {
                    results.Add(canonicalFile);
                }
            }

            foreach (var directory in Directory.GetDirectories(currentDirectory).OrderBy(path => path, StringComparer.Ordinal))
            {
                Recurse(directory);
            }
        }

        Recurse(canonicalRoot);
        return results.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
    }

    private static string GetCanonicalRealPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            try
            {
                FileSystemInfo info = File.Exists(fullPath) ? new FileInfo(fullPath) : new DirectoryInfo(fullPath);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    fullPath = target.FullName;
                }
            }
            catch (Exception)
            {
                // The containment check below remains authoritative when link resolution is unavailable.
            }
        }

        var current = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
        while (!string.IsNullOrEmpty(current) && Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current || Path.GetPathRoot(current) == current)
            {
                break;
            }

            try
            {
                var directory = new DirectoryInfo(current);
                var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    var relative = Path.GetRelativePath(current, fullPath);
                    fullPath = Path.GetFullPath(Path.Combine(target.FullName, relative));
                    current = target.FullName;
                    parent = Path.GetDirectoryName(current);
                }
            }
            catch (Exception)
            {
                // The final containment comparison still rejects paths that can be resolved outside.
            }

            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                break;
            }

            current = parent;
        }

        return Path.GetFullPath(fullPath);
    }

    private static bool IsSubPath(string basePath, string candidatePath)
    {
        var normalizedBase = Path.GetFullPath(basePath).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidatePath).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (normalizedCandidate.Equals(normalizedBase, comparison))
        {
            return true;
        }

        var baseWithSeparator = normalizedBase.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedBase
            : normalizedBase + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(baseWithSeparator, comparison);
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
