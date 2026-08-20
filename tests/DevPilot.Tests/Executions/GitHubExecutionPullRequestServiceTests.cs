using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class GitHubExecutionPullRequestServiceTests
{
    private readonly StubGitHubPullRequestClient _githubClient = new();

    private GitHubExecutionPullRequestService CreateService() =>
        new(_githubClient, NullLogger<GitHubExecutionPullRequestService>.Instance);

    [Fact]
    public async Task CreateOrAdoptPullRequest_ExistingOpenMatchingBranchPR_AdoptsExistingPRWithoutCallingCreate()
    {
        // Arrange
        var service = CreateService();
        var execution = CreateExecution();

        _githubClient.BaseBranchHeadSha = "base_sha";
        _githubClient.HeadBranchHeadSha = execution.CommitSha;

        var existingPr = new GitHubPullRequestDto(
            Number: 42,
            HtmlUrl: "https://github.com/enesscigdem/DevPilot/pull/42",
            State: "open",
            Merged: false,
            ClosedAt: null,
            MergedAt: null,
            HeadRef: execution.BranchName!,
            HeadSha: execution.CommitSha!,
            HeadRepoOwner: "enesscigdem",
            HeadRepoName: "DevPilot",
            BaseRef: "main",
            BaseRepoOwner: "enesscigdem",
            BaseRepoName: "DevPilot",
            Body: "Some existing PR body"
        );

        _githubClient.ConfiguredPullRequests.Add(existingPr);

        // Act
        var result = await service.CreateOrAdoptPullRequestAsync(execution, Guid.NewGuid());

        // Assert
        result.Success.Should().BeTrue();
        result.PullRequestNumber.Should().Be(42);
        result.PullRequestUrl.Should().Be("https://github.com/enesscigdem/DevPilot/pull/42");
        _githubClient.CreateCallCount.Should().Be(0, "Should adopt existing PR during preflight without issuing POST");
    }

    [Fact]
    public async Task CreateOrAdoptPullRequest_CreateReturnsConflict422_RecoversByAdoptingExistingPR()
    {
        // Arrange
        var service = CreateService();
        var execution = CreateExecution();

        _githubClient.BaseBranchHeadSha = "base_sha";
        _githubClient.HeadBranchHeadSha = execution.CommitSha;

        // Preflight list returns empty initially
        _githubClient.ConfiguredPullRequests.Clear();

        // But Create returns 422 Conflict
        _githubClient.CreateConflict = true;

        // Concurrently created PR added to remote
        var concurrentPr = new GitHubPullRequestDto(
            Number: 99,
            HtmlUrl: "https://github.com/enesscigdem/DevPilot/pull/99",
            State: "open",
            Merged: false,
            ClosedAt: null,
            MergedAt: null,
            HeadRef: execution.BranchName!,
            HeadSha: execution.CommitSha!,
            HeadRepoOwner: "enesscigdem",
            HeadRepoName: "DevPilot",
            BaseRef: "main",
            BaseRepoOwner: "enesscigdem",
            BaseRepoName: "DevPilot",
            Body: "Concurrent PR"
        );

        _githubClient.PostConflictPullRequests.Add(concurrentPr);

        // Act
        var result = await service.CreateOrAdoptPullRequestAsync(execution, Guid.NewGuid());

        // Assert
        result.Success.Should().BeTrue();
        result.PullRequestNumber.Should().Be(99);
        result.PullRequestUrl.Should().Be("https://github.com/enesscigdem/DevPilot/pull/99");
    }

    private static TaskExecution CreateExecution()
    {
        var workspace = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "main",
            LocalPath = "/tmp/repo",
            Status = RepositoryWorkspaceStatus.Completed
        };

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            RepositoryWorkspace = workspace,
            Title = "Add GitHub App Integration",
            Description = "Connects GitHub App securely.",
            Status = DevelopmentTaskStatus.Completed
        };

        var commitSha = "1234567890abcdef1234567890abcdef12345678";
        var branchName = "devpilot/task-12345678-87654321";

        return new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            DevelopmentTask = task,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            CommitStatus = ExecutionCommitStatus.Committed,
            CommitSha = commitSha,
            RemoteCommitSha = commitSha,
            BranchName = branchName,
            RemoteBranchName = branchName,
            WorkspacePath = "/tmp/repo",
            PushStatus = ExecutionPushStatus.Pushed
        };
    }

    private sealed class StubGitHubPullRequestClient : IGitHubPullRequestClient
    {
        public string? BaseBranchHeadSha { get; set; } = "sha_base";
        public string? HeadBranchHeadSha { get; set; }
        public List<GitHubPullRequestDto> ConfiguredPullRequests { get; } = new();
        public List<GitHubPullRequestDto> PostConflictPullRequests { get; } = new();
        public bool CreateConflict { get; set; }
        public int CreateCallCount { get; private set; }

        public Task<GitHubBranchRefResult> GetBranchHeadShaAsync(string owner, string repository, string branch, CancellationToken cancellationToken = default)
        {
            if (branch == "main")
            {
                return Task.FromResult(new GitHubBranchRefResult(true, false, BaseBranchHeadSha, null));
            }
            return Task.FromResult(new GitHubBranchRefResult(true, false, HeadBranchHeadSha ?? "sha_head", null));
        }

        public Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>> ListPullRequestsAsync(string owner, string repository, string head, string baseBranch, CancellationToken cancellationToken = default)
        {
            if (CreateCallCount > 0 && PostConflictPullRequests.Count > 0)
            {
                return Task.FromResult(GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>.Success(PostConflictPullRequests));
            }
            return Task.FromResult(GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>.Success(ConfiguredPullRequests));
        }

        public Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> CreatePullRequestAsync(string owner, string repository, string head, string baseBranch, string title, string body, CancellationToken cancellationToken = default)
        {
            CreateCallCount++;
            if (CreateConflict)
            {
                return Task.FromResult(GitHubPullRequestClientResult<GitHubPullRequestDto>.Failure("A pull request already exists for this branch.", isConflict: true));
            }

            var dto = new GitHubPullRequestDto(
                Number: 101,
                HtmlUrl: $"https://github.com/{owner}/{repository}/pull/101",
                State: "open",
                Merged: false,
                ClosedAt: null,
                MergedAt: null,
                HeadRef: head,
                HeadSha: HeadBranchHeadSha ?? "sha_head",
                HeadRepoOwner: owner,
                HeadRepoName: repository,
                BaseRef: baseBranch,
                BaseRepoOwner: owner,
                BaseRepoName: repository,
                Body: body
            );

            return Task.FromResult(GitHubPullRequestClientResult<GitHubPullRequestDto>.Success(dto));
        }

        public Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> GetPullRequestAsync(string owner, string repository, int pullNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(GitHubPullRequestClientResult<GitHubPullRequestDto>.Failure("Not implemented"));

        public Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCheckRunDto>>> ListCheckRunsForRefAsync(string owner, string repository, string refSha, CancellationToken cancellationToken = default) =>
            Task.FromResult(GitHubPullRequestClientResult<IReadOnlyList<GitHubCheckRunDto>>.Success(Array.Empty<GitHubCheckRunDto>()));

        public Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCommitStatusDto>>> ListCommitStatusesForRefAsync(string owner, string repository, string refSha, CancellationToken cancellationToken = default) =>
            Task.FromResult(GitHubPullRequestClientResult<IReadOnlyList<GitHubCommitStatusDto>>.Success(Array.Empty<GitHubCommitStatusDto>()));

        public Task<GitHubPullRequestClientResult<GitHubMergeResultDto>> MergePullRequestAsync(string owner, string repository, int pullNumber, string expectedHeadSha, string? commitTitle = null, string? commitMessage = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(GitHubPullRequestClientResult<GitHubMergeResultDto>.Failure("Not implemented"));
    }
}
