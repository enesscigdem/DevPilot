using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DevPilot.Application.RepositoryClone;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevPilot.Infrastructure.RepositoryClone;

internal sealed class RepositoryCloneService : IRepositoryCloneService
{
    private readonly IOptions<RepositoryCloneOptions> _options;
    private readonly DevPilotDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RepositoryCloneService> _logger;

    public RepositoryCloneService(
        IOptions<RepositoryCloneOptions> options,
        DevPilotDbContext dbContext,
        IConfiguration configuration,
        ILogger<RepositoryCloneService> logger)
    {
        _options = options;
        _dbContext = dbContext;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CloneResult> CloneAsync(CloneRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return ValidationErrorResult("Request is required.");
        }

        var owner = request.Owner?.Trim() ?? string.Empty;
        var repository = request.Repository?.Trim() ?? string.Empty;
        var branch = request.Branch?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(owner))
        {
            return ValidationErrorResult("Owner is required.");
        }

        if (string.IsNullOrWhiteSpace(repository))
        {
            return ValidationErrorResult("Repository is required.");
        }

        if (string.IsNullOrWhiteSpace(branch))
        {
            return ValidationErrorResult("Branch is required.");
        }

        var workspaceRoot = GetWorkspaceRoot();

        string sanitizedOwner;
        string sanitizedRepository;
        string sanitizedBranchPath;

        try
        {
            sanitizedOwner = SanitizePathSegment(owner);
            sanitizedRepository = SanitizePathSegment(repository);
            sanitizedBranchPath = SanitizeBranchPath(branch);
        }
        catch (ArgumentException ex)
        {
            return ValidationErrorResult(ex.Message);
        }

        var targetPath = Path.GetFullPath(
            Path.Combine(workspaceRoot, sanitizedOwner, sanitizedRepository, sanitizedBranchPath));

        if (!IsWithinWorkspaceRoot(targetPath, workspaceRoot))
        {
            return ValidationErrorResult("Target path is outside the workspace root.");
        }

        if (!await IsGitAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return OperationalErrorResult("Git executable is not available.");
        }

        var existingWorkspace = await _dbContext.RepositoryWorkspaces
            .FirstOrDefaultAsync(
                w => w.Owner == owner && w.Repository == repository && w.Branch == branch,
                cancellationToken)
            .ConfigureAwait(false);

        if (Directory.Exists(targetPath))
        {
            return await HandleExistingDirectoryAsync(
                targetPath,
                owner,
                repository,
                branch,
                existingWorkspace,
                cancellationToken).ConfigureAwait(false);
        }

        if (existingWorkspace is not null && existingWorkspace.Status == RepositoryWorkspaceStatus.Cloning)
        {
            return ConflictResult($"Repository workspace '{owner}/{repository}' ({branch}) is currently being cloned.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var now = DateTime.UtcNow;
        if (existingWorkspace is null)
        {
            existingWorkspace = new RepositoryWorkspace
            {
                Id = Guid.NewGuid(),
                Owner = owner,
                Repository = repository,
                Branch = branch,
                LocalPath = targetPath,
                Status = RepositoryWorkspaceStatus.Cloning,
                CreatedAt = now,
                UpdatedAt = now,
            };

            _dbContext.RepositoryWorkspaces.Add(existingWorkspace);
        }
        else
        {
            existingWorkspace.Status = RepositoryWorkspaceStatus.Cloning;
            existingWorkspace.ErrorMessage = null;
            existingWorkspace.CommitSha = string.Empty;
            existingWorkspace.LocalPath = targetPath;
            existingWorkspace.UpdatedAt = now;
            _dbContext.RepositoryWorkspaces.Update(existingWorkspace);
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Concurrency conflict while registering cloning state for {Owner}/{Repository}@{Branch}.", owner, repository, branch);
            return ConflictResult($"Repository workspace '{owner}/{repository}' ({branch}) already exists or is being created.");
        }

        var cloneUrl = $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}.git";
        var token = GetToken();

        string? tempHomeDirectory = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                tempHomeDirectory = CreateAuthenticatedHomeDirectory(token);
            }

            using var timeoutCts = new CancellationTokenSource(_options.Value.Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var cloneOutcome = await RunGitCloneAsync(
                cloneUrl,
                branch,
                targetPath,
                tempHomeDirectory,
                linkedCts.Token)
                .ConfigureAwait(false);

            if (!cloneOutcome.IsSuccess)
            {
                existingWorkspace.Status = RepositoryWorkspaceStatus.Failed;
                existingWorkspace.ErrorMessage = cloneOutcome.ErrorMessage;
                existingWorkspace.UpdatedAt = DateTime.UtcNow;
                _dbContext.RepositoryWorkspaces.Update(existingWorkspace);
                await _dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

                TryDeleteDirectory(targetPath);

                return OperationalErrorResult(cloneOutcome.ErrorMessage ?? "Git clone failed.");
            }

            var commitSha = await ReadHeadCommitShaAsync(targetPath, cancellationToken).ConfigureAwait(false);

            existingWorkspace.Status = RepositoryWorkspaceStatus.Completed;
            existingWorkspace.CommitSha = commitSha;
            existingWorkspace.ErrorMessage = null;
            existingWorkspace.UpdatedAt = DateTime.UtcNow;
            _dbContext.RepositoryWorkspaces.Update(existingWorkspace);
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return SuccessResult(existingWorkspace);
        }
        catch (OperationCanceledException)
        {
            existingWorkspace.Status = RepositoryWorkspaceStatus.Failed;
            existingWorkspace.ErrorMessage = "Clone operation was cancelled or timed out.";
            existingWorkspace.UpdatedAt = DateTime.UtcNow;
            _dbContext.RepositoryWorkspaces.Update(existingWorkspace);
            await _dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            TryDeleteDirectory(targetPath);

            return OperationalErrorResult("Clone operation was cancelled or timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error during clone of {Owner}/{Repository}@{Branch}.",
                owner,
                repository,
                branch);

            existingWorkspace.Status = RepositoryWorkspaceStatus.Failed;
            existingWorkspace.ErrorMessage = ex.Message;
            existingWorkspace.UpdatedAt = DateTime.UtcNow;
            _dbContext.RepositoryWorkspaces.Update(existingWorkspace);
            await _dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            TryDeleteDirectory(targetPath);

            return OperationalErrorResult($"Unexpected error during git clone: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempHomeDirectory) && Directory.Exists(tempHomeDirectory))
            {
                try
                {
                    Directory.Delete(tempHomeDirectory, true);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    private async Task<CloneResult> HandleExistingDirectoryAsync(
        string targetPath,
        string owner,
        string repository,
        string branch,
        RepositoryWorkspace? existingWorkspace,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Target directory exists at {LocalPath} for {Owner}/{Repository}@{Branch}. Verifying repository validity.",
            targetPath,
            owner,
            repository,
            branch);

        var isGitRepo = await IsGitRepositoryAsync(targetPath, cancellationToken).ConfigureAwait(false);
        if (!isGitRepo)
        {
            _logger.LogWarning("Existing directory at {LocalPath} is not a valid git repository.", targetPath);
            return ConflictResult($"The directory at '{targetPath}' already exists but is not a valid git repository.");
        }

        var remoteUrl = await GetRemoteOriginUrlAsync(targetPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(remoteUrl) || !IsRemoteUrlMatch(remoteUrl, owner, repository))
        {
            _logger.LogWarning(
                "Existing directory at {LocalPath} has remote URL '{RemoteUrl}', which does not match requested {Owner}/{Repository}.",
                targetPath,
                remoteUrl,
                owner,
                repository);
            return ConflictResult(
                $"The existing directory at '{targetPath}' has remote origin URL '{remoteUrl ?? "(none)"}', which does not match requested repository '{owner}/{repository}'.");
        }

        var currentBranch = await GetCurrentBranchAsync(targetPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentBranch, branch, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Existing directory at {LocalPath} is on branch '{CurrentBranch}', which does not match requested branch '{Branch}'.",
                targetPath,
                currentBranch,
                branch);
            return ConflictResult(
                $"The existing directory at '{targetPath}' is on branch '{currentBranch ?? "(detached)"}', which does not match requested branch '{branch}'.");
        }

        var commitSha = await ReadHeadCommitShaAsync(targetPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(commitSha))
        {
            _logger.LogWarning("Could not resolve HEAD commit SHA for existing repository at {LocalPath}.", targetPath);
            return ConflictResult($"Could not resolve HEAD commit SHA for existing repository at '{targetPath}'.");
        }

        if (existingWorkspace is not null && existingWorkspace.Status == RepositoryWorkspaceStatus.Completed)
        {
            _logger.LogInformation(
                "Workspace already actively registered in DB for {Owner}/{Repository}@{Branch}.",
                owner,
                repository,
                branch);
            return ConflictResult($"Repository workspace '{owner}/{repository}' ({branch}) already exists.");
        }

        var now = DateTime.UtcNow;
        if (existingWorkspace is null)
        {
            existingWorkspace = new RepositoryWorkspace
            {
                Id = Guid.NewGuid(),
                Owner = owner,
                Repository = repository,
                Branch = branch,
                LocalPath = targetPath,
                CommitSha = commitSha,
                Status = RepositoryWorkspaceStatus.Completed,
                CreatedAt = now,
                UpdatedAt = now,
            };

            _dbContext.RepositoryWorkspaces.Add(existingWorkspace);
        }
        else
        {
            existingWorkspace.Status = RepositoryWorkspaceStatus.Completed;
            existingWorkspace.CommitSha = commitSha;
            existingWorkspace.ErrorMessage = null;
            existingWorkspace.LocalPath = targetPath;
            existingWorkspace.UpdatedAt = now;
            _dbContext.RepositoryWorkspaces.Update(existingWorkspace);
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Concurrency conflict while saving reconnected workspace for {Owner}/{Repository}@{Branch}.", owner, repository, branch);
            return ConflictResult($"Repository workspace '{owner}/{repository}' ({branch}) already exists.");
        }

        _logger.LogInformation(
            "Successfully safely reconnected and registered existing repository workspace {WorkspaceId} at {LocalPath}.",
            existingWorkspace.Id,
            targetPath);

        return SuccessResult(existingWorkspace);
    }

    private static CloneResult ValidationErrorResult(string error)
    {
        return new CloneResult
        {
            Success = false,
            IsValidationError = true,
            Error = error,
        };
    }

    private static CloneResult ConflictResult(string error)
    {
        return new CloneResult
        {
            Success = false,
            IsConflict = true,
            Error = error,
        };
    }

    private static CloneResult OperationalErrorResult(string error)
    {
        return new CloneResult
        {
            Success = false,
            IsValidationError = false,
            IsConflict = false,
            Error = error,
        };
    }

    private static CloneResult SuccessResult(RepositoryWorkspace workspace)
    {
        return new CloneResult
        {
            Success = true,
            WorkspaceId = workspace.Id,
            Owner = workspace.Owner,
            Repository = workspace.Repository,
            Branch = workspace.Branch,
            LocalPath = workspace.LocalPath,
            CommitSha = workspace.CommitSha,
            Status = workspace.Status,
            CreatedAt = workspace.CreatedAt,
            UpdatedAt = workspace.UpdatedAt,
        };
    }

    private string GetWorkspaceRoot()
    {
        var configured = _options.Value.WorkspaceRoot;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DevPilot",
            "Workspaces");

        return Path.GetFullPath(fallback);
    }

    private static string SanitizeBranchPath(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            throw new ArgumentException("Branch cannot be empty.", nameof(branch));
        }

        var segments = branch.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new ArgumentException("Invalid branch name.", nameof(branch));
        }

        var sanitizedSegments = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var sanitized = SanitizePathSegment(segment);
            sanitizedSegments.Add(sanitized);
        }

        return Path.Combine(sanitizedSegments.ToArray());
    }

    private static string SanitizePathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            throw new ArgumentException("Path segment cannot be empty.", nameof(segment));
        }

        var trimmed = segment.Trim();
        if (trimmed == "." || trimmed == ".." || trimmed.Contains(".."))
        {
            throw new ArgumentException($"Invalid path segment: '{segment}'.");
        }

        var builder = new StringBuilder(trimmed);
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            builder.Replace(invalidChar, '_');
        }

        var result = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(result) || result == "." || result == "..")
        {
            throw new ArgumentException($"Invalid path segment: '{segment}'.");
        }

        return result;
    }

    private static bool IsWithinWorkspaceRoot(string targetPath, string workspaceRoot)
    {
        var normalizedTarget = Path.GetFullPath(targetPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedTarget.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRemoteUrlMatch(string? remoteUrl, string owner, string repository)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return false;
        }

        var normalizedUrl = remoteUrl.Trim().Replace('\\', '/');
        if (normalizedUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalizedUrl = normalizedUrl[..^4];
        }
        normalizedUrl = normalizedUrl.TrimEnd('/');

        var expectedSuffix = $"{owner}/{repository}".Replace('\\', '/').Trim('/');

        if (normalizedUrl.Equals(expectedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalizedUrl.EndsWith("/" + expectedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var colonIndex = normalizedUrl.LastIndexOf(':');
        var slashIndex = normalizedUrl.LastIndexOf('/');
        if (colonIndex > 0 && (slashIndex == -1 || colonIndex > slashIndex))
        {
            var scpPath = normalizedUrl[(colonIndex + 1)..].TrimStart('/');
            if (scpPath.Equals(expectedSuffix, StringComparison.OrdinalIgnoreCase) ||
                scpPath.EndsWith("/" + expectedSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string? GetToken()
    {
        return _options.Value.Token
            ?? _configuration["GitProvider:GitHub:Token"]
            ?? _configuration["GITHUB_TOKEN"];
    }

    private static string CreateAuthenticatedHomeDirectory(string token)
    {
        var tempHomeDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHomeDirectory);

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
        var configContent = $"[http]{Environment.NewLine}    extraHeader = \"Authorization: Basic {credentials}\"{Environment.NewLine}";

        File.WriteAllText(Path.Combine(tempHomeDirectory, ".gitconfig"), configContent);

        return tempHomeDirectory;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static async Task<(bool IsSuccess, string? ErrorMessage)> RunGitCloneAsync(
        string cloneUrl,
        string branch,
        string targetPath,
        string? tempHomeDirectory,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(targetPath)!,
        };

        psi.ArgumentList.Add("clone");
        psi.ArgumentList.Add("-b");
        psi.ArgumentList.Add(branch);
        psi.ArgumentList.Add("--single-branch");
        psi.ArgumentList.Add(cloneUrl);
        psi.ArgumentList.Add(targetPath);

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (!string.IsNullOrEmpty(tempHomeDirectory))
        {
            psi.Environment["HOME"] = tempHomeDirectory;
            psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        }

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return (false, $"Git executable is not available: {ex.Message}");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort kill.
            }

            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var message = !string.IsNullOrWhiteSpace(error)
                ? error
                : (!string.IsNullOrWhiteSpace(output) ? output : "Git clone failed with no output.");

            return (false, message);
        }

        return (true, null);
    }

    private static async Task<bool> IsGitRepositoryAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = path,
        };

        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("--is-inside-work-tree");

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);

            return process.ExitCode == 0 && string.Equals(output.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> GetRemoteOriginUrlAsync(string path, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = path,
        };

        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add("--get");
        psi.ArgumentList.Add("remote.origin.url");

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);

            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> GetCurrentBranchAsync(string path, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = path,
        };

        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("--abbrev-ref");
        psi.ArgumentList.Add("HEAD");

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);

            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ReadHeadCommitShaAsync(string repoPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoPath,
        };

        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("HEAD");

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);

            return process.ExitCode == 0 ? output.Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("--version");

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
