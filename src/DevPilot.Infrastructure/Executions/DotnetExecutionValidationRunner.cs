using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Infrastructure validation runner that executes approved dotnet build and test operations
/// exclusively inside an execution Git worktree.
/// </summary>
/// <remarks>
/// TRUST BOUNDARY NOTICE: MSBuild project files (.csproj / .sln) can execute custom build logic
/// and are NOT a security sandbox. This runner is foundation-only for trusted worktree validation.
/// Future pipeline integration for untrusted repositories will require container/sandbox isolation.
/// </remarks>
public sealed class DotnetExecutionValidationRunner : IExecutionValidationRunner
{
    public static readonly TimeSpan DefaultBuildTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultTestTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxAllowedTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MinAllowedTimeout = TimeSpan.FromSeconds(1);

    private static readonly string[] ExcludedDirectoryNames =
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".dotnet_home", ".pnpm-store", "v0-reference"
    };

    private readonly IExecutionWorkspaceManager _workspaceManager;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<DotnetExecutionValidationRunner> _logger;

    public DotnetExecutionValidationRunner(
        IExecutionWorkspaceManager workspaceManager,
        IProcessRunner processRunner,
        ILogger<DotnetExecutionValidationRunner> logger)
    {
        _workspaceManager = workspaceManager ?? throw new ArgumentNullException(nameof(workspaceManager));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BuildValidationResult> ValidateBuildAsync(
        ExecutionValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        var (isValid, errorMessage, timeout, canonicalWorkspace) = await ValidateRequestAndWorkspaceAsync(
            request, DefaultBuildTimeout, cancellationToken).ConfigureAwait(false);

        if (!isValid)
        {
            return BuildValidationResult.FailResult(errorMessage!);
        }

        string targetPath;
        try
        {
            targetPath = ResolveBuildTarget(canonicalWorkspace, request.TargetPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Build target validation failed for workspace '{WorkspacePath}'.", request.WorkspacePath);
            return BuildValidationResult.FailResult(ex.Message);
        }

        var relativeTarget = Path.GetRelativePath(canonicalWorkspace, targetPath);
        _logger.LogInformation("Executing dotnet build for target '{TargetPath}' in workspace '{WorkspacePath}'.", relativeTarget, canonicalWorkspace);

        var arguments = new[] { "build", relativeTarget };
        var processResult = await _processRunner.RunProcessAsync("dotnet", arguments, canonicalWorkspace, timeout, cancellationToken).ConfigureAwait(false);

        return new BuildValidationResult
        {
            Success = processResult.ExitCode == 0 && !processResult.IsTimedOut,
            ExitCode = processResult.ExitCode,
            ErrorMessage = processResult.IsTimedOut ? processResult.ErrorMessage : (processResult.ExitCode != 0 ? $"dotnet build failed with exit code {processResult.ExitCode}." : null),
            StartTime = processResult.StartTime,
            CompletionTime = processResult.CompletionTime,
            Duration = processResult.Duration,
            StdOut = processResult.StdOut,
            StdErr = processResult.StdErr,
            IsTruncated = processResult.IsTruncated,
            IsTimedOut = processResult.IsTimedOut,
            TargetPath = relativeTarget
        };
    }

    public async Task<TestValidationResult> ValidateTestAsync(
        ExecutionValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        var (isValid, errorMessage, timeout, canonicalWorkspace) = await ValidateRequestAndWorkspaceAsync(
            request, DefaultTestTimeout, cancellationToken).ConfigureAwait(false);

        if (!isValid)
        {
            return TestValidationResult.FailResult(errorMessage!);
        }

        string targetPath;
        try
        {
            targetPath = ResolveTestTarget(canonicalWorkspace, request.TargetPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Test target validation failed for workspace '{WorkspacePath}'.", request.WorkspacePath);
            return TestValidationResult.FailResult(ex.Message);
        }

        var relativeTarget = Path.GetRelativePath(canonicalWorkspace, targetPath);
        _logger.LogInformation("Executing dotnet test for target '{TargetPath}' in workspace '{WorkspacePath}'.", relativeTarget, canonicalWorkspace);

        var arguments = new[] { "test", relativeTarget };
        var processResult = await _processRunner.RunProcessAsync("dotnet", arguments, canonicalWorkspace, timeout, cancellationToken).ConfigureAwait(false);

        return new TestValidationResult
        {
            Success = processResult.ExitCode == 0 && !processResult.IsTimedOut,
            ExitCode = processResult.ExitCode,
            ErrorMessage = processResult.IsTimedOut ? processResult.ErrorMessage : (processResult.ExitCode != 0 ? $"dotnet test failed with exit code {processResult.ExitCode}." : null),
            StartTime = processResult.StartTime,
            CompletionTime = processResult.CompletionTime,
            Duration = processResult.Duration,
            StdOut = processResult.StdOut,
            StdErr = processResult.StdErr,
            IsTruncated = processResult.IsTruncated,
            IsTimedOut = processResult.IsTimedOut,
            TargetPath = relativeTarget
        };
    }

    private async Task<(bool IsValid, string? ErrorMessage, TimeSpan Timeout, string CanonicalWorkspace)> ValidateRequestAndWorkspaceAsync(
        ExecutionValidationRequest request,
        TimeSpan defaultTimeout,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return (false, "Execution validation request cannot be null.", defaultTimeout, string.Empty);
        }

        if (string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            return (false, "Workspace path cannot be empty.", defaultTimeout, string.Empty);
        }

        if (string.IsNullOrWhiteSpace(request.BranchName))
        {
            return (false, "Branch name cannot be empty.", defaultTimeout, string.Empty);
        }

        // Validate & Clamp Timeout
        var timeout = defaultTimeout;
        if (request.Timeout.HasValue)
        {
            if (request.Timeout.Value < MinAllowedTimeout)
            {
                return (false, $"Execution timeout value '{request.Timeout.Value}' is invalid. Timeout must be at least {MinAllowedTimeout.TotalSeconds} second.", defaultTimeout, string.Empty);
            }

            if (request.Timeout.Value > MaxAllowedTimeout)
            {
                return (false, $"Execution timeout value '{request.Timeout.Value}' exceeds maximum allowed limit of {MaxAllowedTimeout.TotalMinutes} minutes.", defaultTimeout, string.Empty);
            }

            timeout = request.Timeout.Value;
        }

        // Reuse existing IExecutionWorkspaceManager abstraction for workspace existence & branch verification (dirty worktree allowed for build/test)
        var workspaceVerification = await _workspaceManager.VerifyWorkspaceStateAsync(
            request.WorkspacePath, request.BranchName, requireClean: false, cancellationToken).ConfigureAwait(false);

        if (!workspaceVerification.WorkspaceExists)
        {
            return (false, workspaceVerification.ErrorMessage ?? $"Execution workspace directory does not exist: '{request.WorkspacePath}'.", timeout, string.Empty);
        }

        if (!workspaceVerification.BranchMatches)
        {
            return (false, workspaceVerification.ErrorMessage ?? $"Workspace branch mismatch for '{request.WorkspacePath}'.", timeout, string.Empty);
        }

        var canonicalWorkspace = GetCanonicalRealPath(request.WorkspacePath);
        return (true, null, timeout, canonicalWorkspace);
    }

    public static string ResolveBuildTarget(string canonicalWorkspace, string? callerTargetPath)
    {
        if (!string.IsNullOrWhiteSpace(callerTargetPath))
        {
            return ValidateCallerTargetPath(canonicalWorkspace, callerTargetPath, allowedExtensions: new[] { ".sln", ".csproj" });
        }

        // Auto-discovery: prefer single .sln at workspace root
        var rootSlnFiles = Directory.GetFiles(canonicalWorkspace, "*.sln", SearchOption.TopDirectoryOnly);
        if (rootSlnFiles.Length == 1)
        {
            return GetCanonicalRealPath(rootSlnFiles[0]);
        }

        if (rootSlnFiles.Length > 1)
        {
            throw new InvalidOperationException($"Ambiguous build target: multiple solution (.sln) files found at workspace root: {string.Join(", ", rootSlnFiles.Select(Path.GetFileName))}.");
        }

        // Search workspace for .sln files avoiding excluded & symlink dirs
        var allSlnFiles = SafeFindFiles(canonicalWorkspace, "*.sln");
        if (allSlnFiles.Count == 1)
        {
            return allSlnFiles[0];
        }

        if (allSlnFiles.Count > 1)
        {
            throw new InvalidOperationException($"Ambiguous build target: multiple solution (.sln) files found in workspace: {string.Join(", ", allSlnFiles.Select(p => Path.GetRelativePath(canonicalWorkspace, p)))}.");
        }

        // Fallback: single .csproj file in workspace
        var allCsprojFiles = SafeFindFiles(canonicalWorkspace, "*.csproj");
        if (allCsprojFiles.Count == 1)
        {
            return allCsprojFiles[0];
        }

        if (allCsprojFiles.Count > 1)
        {
            throw new InvalidOperationException($"Ambiguous build target: no solution file found and multiple project (.csproj) files exist: {string.Join(", ", allCsprojFiles.Select(p => Path.GetRelativePath(canonicalWorkspace, p)))}.");
        }

        throw new InvalidOperationException("No solution (.sln) or project (.csproj) file found in workspace for build operation.");
    }

    public static string ResolveTestTarget(string canonicalWorkspace, string? callerTargetPath)
    {
        if (!string.IsNullOrWhiteSpace(callerTargetPath))
        {
            return ValidateCallerTargetPath(canonicalWorkspace, callerTargetPath, allowedExtensions: new[] { ".csproj", ".sln" });
        }

        // Auto-discovery: find test projects matching *.Tests.csproj or *Test*.csproj
        var testProjects = SafeFindFiles(canonicalWorkspace, "*.csproj")
            .Where(p => Path.GetFileName(p).Contains("Test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (testProjects.Count == 1)
        {
            return testProjects[0];
        }

        if (testProjects.Count > 1)
        {
            throw new InvalidOperationException($"Ambiguous test target: multiple test projects found in workspace: {string.Join(", ", testProjects.Select(p => Path.GetRelativePath(canonicalWorkspace, p)))}.");
        }

        // Fallback: check if single .sln exists at workspace root
        var rootSlnFiles = Directory.GetFiles(canonicalWorkspace, "*.sln", SearchOption.TopDirectoryOnly);
        if (rootSlnFiles.Length == 1)
        {
            return GetCanonicalRealPath(rootSlnFiles[0]);
        }

        throw new InvalidOperationException("No test project matching convention (*Test*.csproj) or single root solution (.sln) found in workspace.");
    }

    public static string ValidateCallerTargetPath(string canonicalWorkspace, string callerTargetPath, string[] allowedExtensions)
    {
        if (string.IsNullOrWhiteSpace(callerTargetPath))
        {
            throw new ArgumentException("Target path cannot be empty.", nameof(callerTargetPath));
        }

        // Reject absolute paths
        if (Path.IsPathRooted(callerTargetPath) || callerTargetPath.StartsWith('/') || callerTargetPath.StartsWith('\\'))
        {
            throw new InvalidOperationException($"Absolute target paths are rejected: '{callerTargetPath}'.");
        }

        // Reject path traversal .. or .git
        var segments = callerTargetPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                throw new InvalidOperationException($"Path traversal '..' is rejected in target path: '{callerTargetPath}'.");
            }
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Modification or execution of .git directory is rejected: '{callerTargetPath}'.");
            }
        }

        var ext = Path.GetExtension(callerTargetPath);
        if (!allowedExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Target file extension '{ext}' is not an approved target type ({string.Join(", ", allowedExtensions)}).");
        }

        var combinedPath = Path.Combine(canonicalWorkspace, callerTargetPath);
        var canonicalTarget = GetCanonicalRealPath(combinedPath);

        if (!IsSubPath(canonicalWorkspace, canonicalTarget))
        {
            throw new InvalidOperationException($"Target path safety violation: '{callerTargetPath}' resolves outside the allowed workspace.");
        }

        if (!File.Exists(canonicalTarget))
        {
            throw new InvalidOperationException($"Target file does not exist: '{callerTargetPath}'.");
        }

        return canonicalTarget;
    }

    public static List<string> SafeFindFiles(string rootPath, string searchPattern)
    {
        var results = new List<string>();
        var canonicalRoot = GetCanonicalRealPath(rootPath);

        void Recurse(string currentDir)
        {
            var dirName = Path.GetFileName(currentDir);
            if (ExcludedDirectoryNames.Any(e => e.Equals(dirName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // Verify current directory is not a symlink pointing outside canonicalRoot
            var canonicalCurrentDir = GetCanonicalRealPath(currentDir);
            if (!IsSubPath(canonicalRoot, canonicalCurrentDir))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(currentDir, searchPattern))
            {
                var canonicalFile = GetCanonicalRealPath(file);
                if (IsSubPath(canonicalRoot, canonicalFile))
                {
                    results.Add(canonicalFile);
                }
            }

            foreach (var subDir in Directory.GetDirectories(currentDir))
            {
                Recurse(subDir);
            }
        }

        Recurse(canonicalRoot);
        return results;
    }

    public static string GetCanonicalRealPath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            FileSystemInfo info = File.Exists(fullPath) ? new FileInfo(fullPath) : new DirectoryInfo(fullPath);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target != null)
            {
                fullPath = target.FullName;
            }
        }

        var current = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
        while (!string.IsNullOrEmpty(current) && Directory.Exists(current))
        {
            var dirInfo = new DirectoryInfo(current);
            var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
            if (target != null)
            {
                var relative = Path.GetRelativePath(current, fullPath);
                fullPath = Path.GetFullPath(Path.Combine(target.FullName, relative));
                current = target.FullName;
            }
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }

        return Path.GetFullPath(fullPath);
    }

    private static bool IsSubPath(string basePath, string candidatePath)
    {
        var normBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normCand = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normCand.Equals(normBase, StringComparison.OrdinalIgnoreCase) ||
               normCand.StartsWith(normBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
