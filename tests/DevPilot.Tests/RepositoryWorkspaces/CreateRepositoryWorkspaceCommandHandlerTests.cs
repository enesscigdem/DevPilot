using DevPilot.Application.RepositoryClone;
using DevPilot.Application.RepositoryWorkspaces.Commands.CreateRepositoryWorkspace;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.RepositoryWorkspaces;

public class CreateRepositoryWorkspaceCommandHandlerTests
{
    private readonly FakeRepositoryCloneService _cloneService = new();
    private readonly CreateRepositoryWorkspaceCommandHandler _handler;

    public CreateRepositoryWorkspaceCommandHandlerTests()
    {
        _handler = new CreateRepositoryWorkspaceCommandHandler(
            _cloneService,
            NullLogger<CreateRepositoryWorkspaceCommandHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_NullCommandOrDto_ReturnsValidationError()
    {
        var result1 = await _handler.HandleAsync(null!);
        result1.Success.Should().BeFalse();
        result1.IsValidationError.Should().BeTrue();
        result1.ErrorMessage.Should().Be("Request is required.");

        var result2 = await _handler.HandleAsync(new CreateRepositoryWorkspaceCommand(null!));
        result2.Success.Should().BeFalse();
        result2.IsValidationError.Should().BeTrue();
        result2.ErrorMessage.Should().Be("Request is required.");
    }

    [Theory]
    [InlineData("", "repo", "main", "Owner is required.")]
    [InlineData("   ", "repo", "main", "Owner is required.")]
    [InlineData("owner", "", "main", "Repository is required.")]
    [InlineData("owner", "   ", "main", "Repository is required.")]
    [InlineData("owner", "repo", "", "Branch is required.")]
    [InlineData("owner", "repo", "   ", "Branch is required.")]
    public async Task HandleAsync_MissingRequiredFields_ReturnsValidationError(
        string owner, string repo, string branch, string expectedError)
    {
        var dto = new CreateRepositoryWorkspaceDto
        {
            Owner = owner,
            Repository = repo,
            Branch = branch,
        };

        var result = await _handler.HandleAsync(new CreateRepositoryWorkspaceCommand(dto));

        result.Success.Should().BeFalse();
        result.IsValidationError.Should().BeTrue();
        result.ErrorMessage.Should().Be(expectedError);
    }

    [Fact]
    public async Task HandleAsync_FieldsExceedMaxLengths_ReturnsValidationError()
    {
        var longStr = new string('a', 201);

        var dto1 = new CreateRepositoryWorkspaceDto { Owner = longStr, Repository = "repo", Branch = "main" };
        var res1 = await _handler.HandleAsync(new CreateRepositoryWorkspaceCommand(dto1));
        res1.Success.Should().BeFalse();
        res1.IsValidationError.Should().BeTrue();
        res1.ErrorMessage.Should().Be("Owner must be at most 200 characters.");

        var dto2 = new CreateRepositoryWorkspaceDto { Owner = "owner", Repository = longStr, Branch = "main" };
        var res2 = await _handler.HandleAsync(new CreateRepositoryWorkspaceCommand(dto2));
        res2.Success.Should().BeFalse();
        res2.IsValidationError.Should().BeTrue();
        res2.ErrorMessage.Should().Be("Repository must be at most 200 characters.");

        var dto3 = new CreateRepositoryWorkspaceDto { Owner = "owner", Repository = "repo", Branch = longStr };
        var res3 = await _handler.HandleAsync(new CreateRepositoryWorkspaceCommand(dto3));
        res3.Success.Should().BeFalse();
        res3.IsValidationError.Should().BeTrue();
        res3.ErrorMessage.Should().Be("Branch must be at most 200 characters.");
    }

    [Theory]
    [InlineData("../evil", "repo", "main", "Owner contains invalid characters.")]
    [InlineData("owner/sub", "repo", "main", "Owner contains invalid characters.")]
    [InlineData("owner", "../evil", "main", "Repository contains invalid characters.")]
    [InlineData("owner", "repo/sub", "main", "Repository contains invalid characters.")]
    [InlineData("owner", "repo", "../evil", "Branch contains invalid characters or path traversal sequences.")]
    [InlineData("owner", "repo", "/leading-slash", "Branch contains invalid characters or path traversal sequences.")]
    [InlineData("owner", "repo", "trailing-slash/", "Branch contains invalid characters or path traversal sequences.")]
    public async Task HandleAsync_InvalidCharactersOrPathTraversal_ReturnsValidationError(
        string owner, string repo, string branch, string expectedError)
    {
        var dto = new CreateRepositoryWorkspaceDto
        {
            Owner = owner,
            Repository = repo,
            Branch = branch,
        };

        var result = await _handler.HandleAsync(new CreateRepositoryWorkspaceCommand(dto));

        result.Success.Should().BeFalse();
        result.IsValidationError.Should().BeTrue();
        result.ErrorMessage.Should().Be(expectedError);
    }

    [Fact]
    public async Task HandleAsync_LegitimateBranchWithSlash_PassesValidationAndCallsService()
    {
        var workspaceId = Guid.NewGuid();
        _cloneService.ResultToReturn = new CloneResult
        {
            Success = true,
            WorkspaceId = workspaceId,
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "feature/my-change",
            CommitSha = "abc1234",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var dto = new CreateRepositoryWorkspaceDto
        {
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "feature/my-change",
        };

        var result = await _handler.HandleAsync(new CreateRepositoryWorkspaceCommand(dto));

        result.Success.Should().BeTrue();
        result.Workspace.Should().NotBeNull();
        result.Workspace!.Id.Should().Be(workspaceId);
        result.Workspace.Owner.Should().Be("enesscigdem");
        result.Workspace.Repository.Should().Be("DevPilot");
        result.Workspace.Branch.Should().Be("feature/my-change");
        result.Workspace.CommitSha.Should().Be("abc1234");
        result.Workspace.Status.Should().Be(RepositoryWorkspaceStatus.Completed);
    }

    [Fact]
    public async Task HandleAsync_CloneServiceReturnsConflict_ReturnsConflictResult()
    {
        _cloneService.ResultToReturn = new CloneResult
        {
            Success = false,
            IsConflict = true,
            Error = "Repository workspace 'enesscigdem/DevPilot' (master) already exists.",
        };

        var dto = new CreateRepositoryWorkspaceDto
        {
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "master",
        };

        var result = await _handler.HandleAsync(new CreateRepositoryWorkspaceCommand(dto));

        result.Success.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.IsValidationError.Should().BeFalse();
        result.ErrorMessage.Should().Be("Repository workspace 'enesscigdem/DevPilot' (master) already exists.");
        result.Workspace.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_CloneServiceReturnsOperationalFailure_ReturnsFailureResult()
    {
        _cloneService.ResultToReturn = new CloneResult
        {
            Success = false,
            IsConflict = false,
            IsValidationError = false,
            Error = "Git clone failed: Remote repository not found.",
        };

        var dto = new CreateRepositoryWorkspaceDto
        {
            Owner = "enesscigdem",
            Repository = "NonExistent",
            Branch = "master",
        };

        var result = await _handler.HandleAsync(new CreateRepositoryWorkspaceCommand(dto));

        result.Success.Should().BeFalse();
        result.IsConflict.Should().BeFalse();
        result.IsValidationError.Should().BeFalse();
        result.ErrorMessage.Should().Be("Git clone failed: Remote repository not found.");
        result.Workspace.Should().BeNull();
    }

    private sealed class FakeRepositoryCloneService : IRepositoryCloneService
    {
        public CloneResult ResultToReturn { get; set; } = new();

        public Task<CloneResult> CloneAsync(CloneRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResultToReturn);
        }
    }
}
