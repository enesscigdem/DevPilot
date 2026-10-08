using DevPilot.Application.GitProviders;
using DevPilot.Application.RepositoryClone;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.RepositoryWorkspaces.Commands.CreateRepositoryWorkspace;

public interface ICreateRepositoryWorkspaceCommandHandler
{
    Task<CreateRepositoryWorkspaceResult> HandleAsync(
        CreateRepositoryWorkspaceCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class CreateRepositoryWorkspaceCommandHandler : ICreateRepositoryWorkspaceCommandHandler
{
    private readonly IRepositoryCloneService _cloneService;
    private readonly ILogger<CreateRepositoryWorkspaceCommandHandler> _logger;
    private readonly IGitConnectionStore? _connectionStore;

    public CreateRepositoryWorkspaceCommandHandler(
        IRepositoryCloneService cloneService,
        ILogger<CreateRepositoryWorkspaceCommandHandler> logger,
        IGitConnectionStore? connectionStore = null)
    {
        _cloneService = cloneService;
        _logger = logger;
        _connectionStore = connectionStore;
    }

    public async Task<CreateRepositoryWorkspaceResult> HandleAsync(
        CreateRepositoryWorkspaceCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command is null || command.Dto is null)
        {
            return ValidationError("Request is required.");
        }

        var dto = command.Dto;
        var cloneRequest = new CloneRequest
        {
            Owner = dto.Owner?.Trim() ?? string.Empty,
            Repository = dto.Repository?.Trim() ?? string.Empty,
            Branch = dto.Branch?.Trim() ?? string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(dto.RemoteUrl))
        {
            var remoteError = await ApplyRemoteUrlAsync(dto, cloneRequest, cancellationToken).ConfigureAwait(false);
            if (remoteError is not null)
            {
                return ValidationError(remoteError);
            }
        }

        var validationError = Validate(cloneRequest);
        if (!string.IsNullOrEmpty(validationError))
        {
            return ValidationError(validationError);
        }

        var cloneResult = await _cloneService
            .CloneAsync(cloneRequest, cancellationToken)
            .ConfigureAwait(false);

        if (!cloneResult.Success)
        {
            _logger.LogWarning(
                "Failed to provision repository workspace for {Owner}/{Repository}@{Branch}. Conflict: {IsConflict}, Error: {Error}",
                cloneRequest.Owner,
                cloneRequest.Repository,
                cloneRequest.Branch,
                cloneResult.IsConflict,
                cloneResult.Error);

            return new CreateRepositoryWorkspaceResult
            {
                Success = false,
                IsValidationError = cloneResult.IsValidationError,
                IsConflict = cloneResult.IsConflict,
                ErrorMessage = cloneResult.Error ?? "Failed to create repository workspace.",
            };
        }

        _logger.LogInformation(
            "Successfully provisioned repository workspace {WorkspaceId} for {Owner}/{Repository}@{Branch}.",
            cloneResult.WorkspaceId,
            cloneResult.Owner,
            cloneResult.Repository,
            cloneResult.Branch);

        return new CreateRepositoryWorkspaceResult
        {
            Success = true,
            Workspace = new RepositoryWorkspaceDto
            {
                Id = cloneResult.WorkspaceId,
                Provider = cloneRequest.Provider.ToString(),
                Host = cloneRequest.Host,
                Owner = cloneResult.Owner,
                Repository = cloneResult.Repository,
                Branch = cloneResult.Branch,
                Status = cloneResult.Status,
                CommitSha = cloneResult.CommitSha,
                CreatedAt = cloneResult.CreatedAt,
                UpdatedAt = cloneResult.UpdatedAt,
            },
        };
    }

    private async Task<string?> ApplyRemoteUrlAsync(
        CreateRepositoryWorkspaceDto dto,
        CloneRequest cloneRequest,
        CancellationToken cancellationToken)
    {
        var parsed = GitRemoteUrl.Parse(dto.RemoteUrl);
        if (parsed is null)
        {
            return "Remote URL is not a valid https or ssh git URL (expected https://host/group/repo.git).";
        }

        if (string.Equals(parsed.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub repositories are connected through the GitHub App. Pick the repository from the GitHub list.";
        }

        var provider = GitRemoteUrl.InferProvider(parsed.Host);
        if (dto.GitConnectionId.HasValue)
        {
            if (_connectionStore is null)
            {
                return "Git connections are not available.";
            }

            var connection = await _connectionStore.GetAsync(dto.GitConnectionId.Value, cancellationToken).ConfigureAwait(false);
            if (connection is null)
            {
                return "Git connection not found.";
            }

            if (!string.Equals(connection.Host, parsed.Host, StringComparison.OrdinalIgnoreCase))
            {
                return $"The selected connection is for '{connection.Host}', but the URL points to '{parsed.Host}'.";
            }

            provider = connection.Provider;
        }

        cloneRequest.Provider = provider;
        cloneRequest.Host = parsed.Host;
        cloneRequest.GitConnectionId = dto.GitConnectionId;
        cloneRequest.Owner = parsed.Owner;
        cloneRequest.Repository = parsed.Repository;
        return null;
    }

    private static CreateRepositoryWorkspaceResult ValidationError(string message) =>
        new()
        {
            Success = false,
            IsValidationError = true,
            ErrorMessage = message,
        };

    private static string? Validate(CloneRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Owner))
        {
            return "Owner is required.";
        }

        if (string.IsNullOrWhiteSpace(request.Repository))
        {
            return "Repository is required.";
        }

        if (string.IsNullOrWhiteSpace(request.Branch))
        {
            return "Branch is required.";
        }

        var owner = request.Owner;
        var repo = request.Repository;
        var branch = request.Branch;
        var allowNestedOwner = request.Provider != GitProviderKind.GitHub;

        if (owner.Length > 200)
        {
            return "Owner must be at most 200 characters.";
        }

        if (repo.Length > 200)
        {
            return "Repository must be at most 200 characters.";
        }

        if (branch.Length > 200)
        {
            return "Branch must be at most 200 characters.";
        }

        if ((!allowNestedOwner && owner.Contains('/')) || owner.Contains('\\') || owner.Contains(".."))
        {
            return "Owner contains invalid characters.";
        }

        if (repo.Contains('/') || repo.Contains('\\') || repo.Contains(".."))
        {
            return "Repository contains invalid characters.";
        }

        if (branch.Contains("..") || branch.StartsWith('/') || branch.EndsWith('/') || branch.StartsWith('\\') || branch.EndsWith('\\'))
        {
            return "Branch contains invalid characters or path traversal sequences.";
        }

        return null;
    }
}
