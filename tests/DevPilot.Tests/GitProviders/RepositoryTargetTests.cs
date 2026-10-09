using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.GitProviders;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DevPilot.Tests.GitProviders;

/// <summary>The same owner/repository connected on two hosts must resolve by the workspace the call is for, never by recency.</summary>
public sealed class RepositoryTargetTests
{
    private readonly DevPilotDbContext _db = new(new DbContextOptionsBuilder<DevPilotDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private readonly RepositoryWorkspace _github = Workspace(GitProviderKind.GitHub, "github.com", DateTime.UtcNow.AddDays(-2));
    private readonly RepositoryWorkspace _gitlab = Workspace(GitProviderKind.GitLab, "gitlab.company.com", DateTime.UtcNow);

    private static RepositoryWorkspace Workspace(GitProviderKind provider, string host, DateTime updated) => new()
    {
        Id = Guid.NewGuid(), Owner = "team", Repository = "app", Branch = "main", Provider = provider, Host = host, UpdatedAt = updated,
    };

    public RepositoryTargetTests()
    {
        _db.RepositoryWorkspaces.AddRange(_github, _gitlab);
        _db.SaveChanges();
    }

    private (RoutingPullRequestClient Router, RepositoryTargetContext Target) Router()
    {
        var target = new RepositoryTargetContext();
        var router = new RoutingPullRequestClient(_db, null!, null!, null!, null!, target);
        return (router, target);
    }

    [Fact]
    public async Task Without_a_target_the_same_repository_on_two_hosts_is_refused_not_guessed()
    {
        var (router, _) = Router();

        var result = await router.GetPullRequestAsync("team", "app", 1);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("more than one git host");
    }

    [Fact]
    public async Task The_credential_lookup_without_a_target_is_refused_too()
    {
        var resolver = new GitCredentialResolver(null!, null!, _db);

        var result = await resolver.ResolveForRepositoryAsync("team", "app");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("more than one git host");
    }


    [Fact]
    public async Task With_a_target_the_pull_request_call_is_not_ambiguous_whatever_was_updated_last()
    {
        var generic = Workspace(GitProviderKind.Generic, "git.company.com", DateTime.UtcNow.AddDays(-9));
        _db.RepositoryWorkspaces.Add(generic);
        _db.SaveChanges();
        var (router, target) = Router();
        target.Use(generic.Id);

        var result = await router.GetPullRequestAsync("team", "app", 1);

        // The generic host (updated least recently) was chosen because the caller named it: it has no pull request API.
        result.ErrorMessage.Should().Contain("no pull request API");
    }

    [Fact]
    public async Task With_a_target_the_credential_lookup_is_not_ambiguous()
    {
        var target = new RepositoryTargetContext();
        target.Use(_gitlab.Id);
        var resolver = new GitCredentialResolver(null!, null!, _db, target);

        var result = await resolver.ResolveForRepositoryAsync("team", "app");

        result.ErrorMessage.Should().NotContain("more than one git host");
    }
    [Fact]
    public async Task A_target_that_is_not_this_repository_is_ignored()
    {
        var other = Workspace(GitProviderKind.GitHub, "github.com", DateTime.UtcNow);
        other.Repository = "different";
        _db.RepositoryWorkspaces.Add(other);
        _db.SaveChanges();
        var (router, target) = Router();
        target.Use(other.Id);

        var result = await router.GetPullRequestAsync("team", "app", 1);

        result.ErrorMessage.Should().Contain("more than one git host");
    }
}
