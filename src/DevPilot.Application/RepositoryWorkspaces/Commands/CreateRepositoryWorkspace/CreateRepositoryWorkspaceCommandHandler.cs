using DevPilot.Application.RepositoryClone;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
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

    public CreateRepositoryWorkspaceCommandHandler(
        IRepositoryCloneService cloneService,
        ILogger<CreateRepositoryWorkspaceCommandHandler> logger)
    {
        _cloneService = cloneService;
        _logger = logger;
    }

    public async Task<CreateRepositoryWorkspaceResult> HandleAsync(
        CreateRepositoryWorkspaceCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command is null || command.Dto is null)
        {
            return new CreateRepositoryWorkspaceResult
            {
                Success = false,
                IsValidationError = true,
                ErrorMessage = "Request is required.",
            };
        }

        var dto = command.Dto;
        var validationError = Validate(dto);
        if (!string.IsNullOrEmpty(validationError))
        {
            return new CreateRepositoryWorkspaceResult
            {
                Success = false,
                IsValidationError = true,
                ErrorMessage = validationError,
            };
        }

        var cloneRequest = new CloneRequest
        {
            Owner = dto.Owner.Trim(),
            Repository = dto.Repository.Trim(),
            Branch = dto.Branch.Trim(),
        };

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

    private static string? Validate(CreateRepositoryWorkspaceDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Owner))
        {
            return "Owner is required.";
        }

        if (string.IsNullOrWhiteSpace(dto.Repository))
        {
            return "Repository is required.";
        }

        if (string.IsNullOrWhiteSpace(dto.Branch))
        {
            return "Branch is required.";
        }

        var owner = dto.Owner.Trim();
        var repo = dto.Repository.Trim();
        var branch = dto.Branch.Trim();

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

        if (owner.Contains('/') || owner.Contains('\\') || owner.Contains(".."))
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
