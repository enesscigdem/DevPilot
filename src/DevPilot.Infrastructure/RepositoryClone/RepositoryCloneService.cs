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
            return ErrorResult("Request is required.");
        }

        var owner = request.Owner?.Trim() ?? string.Empty;
        var repository = request.Repository?.Trim() ?? string.Empty;
        var branch = request.Branch?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(owner))
        {
            return ErrorResult("Owner is required.");
        }

        if (string.IsNullOrWhiteSpace(repository))
        {
            return ErrorResult("Repository is required.");
        }

        if (string.IsNullOrWhiteSpace(branch))
        {
            return ErrorResult("Branch is required.");
        }

        var workspaceRoot = GetWorkspaceRoot();

        string sanitizedOwner;
        string sanitizedRepository;
        string sanitizedBranch;

        try
        {
            sanitizedOwner = SanitizePathSegment(owner);
            sanitizedRepository = SanitizePathSegment(repository);
            sanitizedBranch = SanitizePathSegment(branch);
        }
        catch (ArgumentException ex)
        {
            return ErrorResult(ex.Message);
        }

        var targetPath = Path.GetFullPath(
            Path.Combine(workspaceRoot, sanitizedOwner, sanitizedRepository, sanitizedBranch));

        if (!IsWithinWorkspaceRoot(targetPath, workspaceRoot))
        {
            return ErrorResult("Target path is outside the workspace root.");
        }

        if (!await IsGitAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return ErrorResult("Git executable is not available.");
        }

        if (Directory.Exists(targetPath))
        {
            _logger.LogWarning(
                "Workspace already exists for {Owner}/{Repository}@{Branch} at {LocalPath}.",
                owner,
                repository,
                branch,
                targetPath);

            var existingWorkspace = await _dbContext.RepositoryWorkspaces
                .FirstOrDefaultAsync(
                    w => w.Owner == owner && w.Repository == repository && w.Branch == branch,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existingWorkspace is not null)
            {
                existingWorkspace.Status = RepositoryWorkspaceStatus.AlreadyExists;
                existingWorkspace.UpdatedAt = DateTime.UtcNow;
                _dbContext.RepositoryWorkspaces.Update(existingWorkspace);
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return ErrorResult("Workspace already exists for this repository and branch.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var workspace = await _dbContext.RepositoryWorkspaces
            .FirstOrDefaultAsync(
                w => w.Owner == owner && w.Repository == repository && w.Branch == branch,
                cancellationToken)
            .ConfigureAwait(false);

        if (workspace is null)
        {
            workspace = new RepositoryWorkspace
            {
                Id = Guid.NewGuid(),
                Owner = owner,
                Repository = repository,
                Branch = branch,
                LocalPath = targetPath,
                Status = RepositoryWorkspaceStatus.Cloning,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            _dbContext.RepositoryWorkspaces.Add(workspace);
        }
        else
        {
            workspace.Status = RepositoryWorkspaceStatus.Cloning;
            workspace.ErrorMessage = null;
            workspace.CommitSha = string.Empty;
            workspace.LocalPath = targetPath;
            workspace.UpdatedAt = DateTime.UtcNow;
            _dbContext.RepositoryWorkspaces.Update(workspace);
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

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
                workspace.Status = RepositoryWorkspaceStatus.Failed;
                workspace.ErrorMessage = cloneOutcome.ErrorMessage;
                workspace.UpdatedAt = DateTime.UtcNow;
                _dbContext.RepositoryWorkspaces.Update(workspace);
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                TryDeleteDirectory(targetPath);

                return ErrorResult(cloneOutcome.ErrorMessage ?? "Git clone failed.");
            }

            var commitSha = await ReadHeadCommitShaAsync(targetPath, cancellationToken).ConfigureAwait(false);

            workspace.Status = RepositoryWorkspaceStatus.Completed;
            workspace.CommitSha = commitSha;
            workspace.ErrorMessage = null;
            workspace.UpdatedAt = DateTime.UtcNow;
            _dbContext.RepositoryWorkspaces.Update(workspace);
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new CloneResult
            {
                LocalPath = targetPath,
                Branch = branch,
                CommitSha = commitSha,
                Success = true,
            };
        }
        catch (OperationCanceledException)
        {
            workspace.Status = RepositoryWorkspaceStatus.Failed;
            workspace.ErrorMessage = "Clone operation was cancelled or timed out.";
            workspace.UpdatedAt = DateTime.UtcNow;
            _dbContext.RepositoryWorkspaces.Update(workspace);
            await _dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            TryDeleteDirectory(targetPath);

            return ErrorResult("Clone operation was cancelled or timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error during clone of {Owner}/{Repository}@{Branch}.",
                owner,
                repository,
                branch);

            workspace.Status = RepositoryWorkspaceStatus.Failed;
            workspace.ErrorMessage = ex.Message;
            workspace.UpdatedAt = DateTime.UtcNow;
            _dbContext.RepositoryWorkspaces.Update(workspace);
            await _dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            TryDeleteDirectory(targetPath);

            return ErrorResult($"Unexpected error: {ex.Message}");
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

    private static CloneResult ErrorResult(string error)
    {
        return new CloneResult
        {
            Success = false,
            Error = error,
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

    private static string SanitizePathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            throw new ArgumentException("Path segment cannot be empty.", nameof(segment));
        }

        var builder = new StringBuilder(segment.Trim());
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            builder.Replace(invalidChar, '_');
        }

        builder.Replace('/', '_');
        builder.Replace('\\', '_');

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

        using var process = new Process { StartInfo = psi };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return output.Trim();
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
