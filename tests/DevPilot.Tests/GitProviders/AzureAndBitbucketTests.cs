using System.Net;
using System.Text;
using System.Text.Json;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.GitProviders;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.Executions;
using DevPilot.Infrastructure.GitProviders;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.GitProviders;

public class AzureAndBitbucketUrlTests
{
    [Theory]
    [InlineData("https://dev.azure.com/acme/Shop/_git/api", "acme/Shop", "api")]
    [InlineData("https://acme@dev.azure.com/acme/Shop/_git/api", "acme/Shop", "api")]
    [InlineData("https://dev.azure.com/acme/My%20Project/_git/web%20app", "acme/My Project", "web app")]
    [InlineData("https://dev.azure.com/acme/_git/api", "acme/api", "api")]
    [InlineData("https://acme.visualstudio.com/Shop/_git/api", "acme/Shop", "api")]
    [InlineData("https://acme.visualstudio.com/DefaultCollection/Shop/_git/api", "acme/Shop", "api")]
    [InlineData("git@ssh.dev.azure.com:v3/acme/Shop/api", "acme/Shop", "api")]
    [InlineData("ssh://git@ssh.dev.azure.com/v3/acme/Shop/api", "acme/Shop", "api")]
    public void AzureAddressesInEveryStyleGiveTheSameIdentity(string url, string owner, string repo)
    {
        var parsed = GitRemoteUrl.Parse(url);

        parsed.Should().NotBeNull();
        parsed!.Host.Should().Be("dev.azure.com");
        parsed.Owner.Should().Be(owner);
        parsed.Repository.Should().Be(repo);
    }

    [Theory]
    [InlineData("https://dev.azure.com/acme/Shop/api")]
    [InlineData("https://dev.azure.com/_git/api")]
    [InlineData("https://dev.azure.com/acme/Shop/_git")]
    [InlineData("git@ssh.dev.azure.com:v3/acme/Shop")]
    [InlineData("https://dev.azure.com/acme/../_git/api")]
    public void MalformedAzureAddressesAreRejected(string url)
    {
        GitRemoteUrl.Parse(url).Should().BeNull();
    }

    [Theory]
    [InlineData("https://bitbucket.org/acme/api.git", "acme", "api")]
    [InlineData("https://user@bitbucket.org/acme/api.git", "acme", "api")]
    [InlineData("git@bitbucket.org:acme/api.git", "acme", "api")]
    public void BitbucketAddressesAreParsed(string url, string owner, string repo)
    {
        var parsed = GitRemoteUrl.Parse(url)!;

        parsed.Host.Should().Be("bitbucket.org");
        parsed.Owner.Should().Be(owner);
        parsed.Repository.Should().Be(repo);
    }

    [Theory]
    [InlineData("dev.azure.com", GitProviderKind.AzureDevOps)]
    [InlineData("ssh.dev.azure.com", GitProviderKind.AzureDevOps)]
    [InlineData("acme.visualstudio.com", GitProviderKind.AzureDevOps)]
    [InlineData("bitbucket.org", GitProviderKind.Bitbucket)]
    [InlineData("BITBUCKET.ORG", GitProviderKind.Bitbucket)]
    [InlineData("github.com", GitProviderKind.GitHub)]
    [InlineData("gitlab.com", GitProviderKind.GitLab)]
    [InlineData("git.acme.local", GitProviderKind.Generic)]
    [InlineData("bitbucket.acme.local", GitProviderKind.Generic)]
    [InlineData("visualstudio.com.evil.io", GitProviderKind.Generic)]
    public void TheProviderIsInferredFromTheHost(string host, GitProviderKind expected)
    {
        GitRemoteUrl.InferProvider(host).Should().Be(expected);
    }

    [Fact]
    public void AzureCloneUrlsNameTheRepositoryUnderGit_WithoutADotGitSuffix()
    {
        GitRemoteUrl.BuildCloneUrl(GitProviderKind.AzureDevOps, "dev.azure.com", "acme/My Project", "web app")
            .Should().Be("https://dev.azure.com/acme/My%20Project/_git/web%20app");
    }

    [Fact]
    public void BitbucketCloneUrlsKeepTheDotGitSuffix()
    {
        GitRemoteUrl.BuildCloneUrl(GitProviderKind.Bitbucket, "bitbucket.org", "acme", "api")
            .Should().Be("https://bitbucket.org/acme/api.git");
    }

    [Theory]
    [InlineData("https://dev.azure.com/acme/Shop/_git/api")]
    [InlineData("https://acme.visualstudio.com/Shop/_git/api")]
    [InlineData("git@ssh.dev.azure.com:v3/acme/Shop/api")]
    public void AnAzureRemoteMatchesItsWorkspaceWhateverTheAddressStyle(string origin)
    {
        GitRemoteUrlNormalizer.MatchesRepository(origin, "acme/Shop", "api").Should().BeTrue();
        GitRemoteUrlNormalizer.MatchesRepository(origin, "acme/Other", "api").Should().BeFalse();
    }

    [Fact]
    public void TheSameCloneUrlRoundTripsThroughTheParser()
    {
        var url = GitRemoteUrl.BuildCloneUrl(GitProviderKind.AzureDevOps, "dev.azure.com", "acme/My Project", "web app");

        var parsed = GitRemoteUrl.Parse(url)!;

        parsed.Owner.Should().Be("acme/My Project");
        parsed.Repository.Should().Be("web app");
    }

    [Theory]
    [InlineData(GitProviderKind.GitLab, "oauth2")]
    [InlineData(GitProviderKind.Bitbucket, "x-token-auth")]
    [InlineData(GitProviderKind.AzureDevOps, "git")]
    [InlineData(GitProviderKind.Generic, "git")]
    public void EachProviderGetsItsDefaultUserName(GitProviderKind provider, string expected)
    {
        GitCredentialResolver.DefaultUsername(provider).Should().Be(expected);
    }

    [Fact]
    public void TrustedPullRequestLinksAreBuiltPerProvider()
    {
        string Url(GitProviderKind provider, string owner, string repo) =>
            GitHubExecutionPullRequestService.BuildTrustedPrUrl(
                new RepositoryWorkspace { Provider = provider, Host = "h", Owner = owner, Repository = repo }, owner, repo, 7);

        Url(GitProviderKind.AzureDevOps, "acme/My Project", "web app")
            .Should().Be("https://dev.azure.com/acme/My%20Project/_git/web%20app/pullrequest/7");
        Url(GitProviderKind.Bitbucket, "acme", "api").Should().Be("https://bitbucket.org/acme/api/pull-requests/7");
    }
}

public class AzureDevOpsClientTests
{
    private const string Sha = "1111111111111111111111111111111111111111";

    private static (AzureDevOpsPullRequestClient Client, Http Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Http(respond);
        var client = new AzureDevOpsPullRequestClient(
            new Factory(handler), new FixedCredentials("pat-123", "git", "dev.azure.com"),
            NullLogger<AzureDevOpsPullRequestClient>.Instance, TimeSpan.Zero);
        return (client, handler);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Pr(int id, string status = "active", string mergeStatus = "succeeded", string? description = "body") =>
        JsonSerializer.Serialize(new
        {
            pullRequestId = id,
            status,
            mergeStatus,
            sourceRefName = "refs/heads/devpilot/task-1",
            targetRefName = "refs/heads/main",
            description,
            closedDate = status == "active" ? null : "2026-10-06T10:00:00Z",
            lastMergeSourceCommit = new { commitId = Sha },
            lastMergeCommit = new { commitId = "2222222222222222222222222222222222222222" },
        });

    [Fact]
    public async Task EveryCallCarriesTheTokenAsTheBasicAuthPasswordWithAnEmptyUserAndTheApiVersion()
    {
        var (client, http) = Create(_ => Json("{\"value\":[]}"));

        await client.ListPullRequestsAsync("acme/Shop", "api", "devpilot/task-1", "main");

        var request = http.Requests.Single();
        request.Headers.Authorization!.Scheme.Should().Be("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)).Should().Be(":pat-123");
        request.RequestUri!.Host.Should().Be("dev.azure.com");
        request.RequestUri.AbsolutePath.Should().Be("/acme/Shop/_apis/git/repositories/api/pullrequests");
        request.RequestUri.Query.Should().Contain("api-version=7.1");
    }

    [Fact]
    public async Task ListingFiltersByBothBranchesAcrossAllStates()
    {
        var (client, http) = Create(_ => Json("{\"value\":[]}"));

        await client.ListPullRequestsAsync("acme/Shop", "api", "devpilot/task-1", "main");

        var query = Uri.UnescapeDataString(http.Requests.Single().RequestUri!.Query);
        query.Should().Contain("searchCriteria.status=all")
            .And.Contain("searchCriteria.sourceRefName=refs/heads/devpilot/task-1")
            .And.Contain("searchCriteria.targetRefName=refs/heads/main");
    }

    [Fact]
    public async Task APullRequestIsMappedWithItsFullHeadAndTheTrustedLink()
    {
        var (client, _) = Create(_ => Json($"{{\"value\":[{Pr(42)}]}}"));

        var result = await client.ListPullRequestsAsync("acme/Shop", "api", "devpilot/task-1", "main");

        var pr = result.Data!.Single();
        pr.Number.Should().Be(42);
        pr.State.Should().Be("open");
        pr.Merged.Should().BeFalse();
        pr.HeadRef.Should().Be("devpilot/task-1");
        pr.BaseRef.Should().Be("main");
        pr.HeadSha.Should().Be(Sha);
        pr.Body.Should().Be("body");
        pr.HtmlUrl.Should().Be("https://dev.azure.com/acme/Shop/_git/api/pullrequest/42");
        pr.MergeableState.Should().BeNull();
    }

    [Theory]
    [InlineData("active", "open", false)]
    [InlineData("completed", "closed", true)]
    [InlineData("abandoned", "closed", false)]
    public async Task PullRequestStatesAreTranslated(string status, string state, bool merged)
    {
        var (client, _) = Create(_ => Json(Pr(5, status)));

        var pr = (await client.GetPullRequestAsync("acme/Shop", "api", 5)).Data!;

        pr.State.Should().Be(state);
        pr.Merged.Should().Be(merged);
        (pr.MergedAt.HasValue).Should().Be(merged);
    }

    [Fact]
    public async Task AConflictingPullRequestIsMarkedDirty()
    {
        var (client, _) = Create(_ => Json(Pr(5, mergeStatus: "conflicts")));

        (await client.GetPullRequestAsync("acme/Shop", "api", 5)).Data!.MergeableState.Should().Be("dirty");
    }

    [Fact]
    public async Task TheBranchLookupIgnoresBranchesThatOnlySharePrefix()
    {
        var (client, _) = Create(_ => Json($$"""{"value":[{"name":"refs/heads/feature-2","objectId":"{{new string('b', 40)}}"},{"name":"refs/heads/feature","objectId":"{{Sha}}"}]}"""));

        var result = await client.GetBranchHeadShaAsync("acme/Shop", "api", "feature");

        result.IsSuccess.Should().BeTrue();
        result.Sha.Should().Be(Sha);
    }

    [Fact]
    public async Task AMissingBranchIsReportedAsNotFound()
    {
        var (client, _) = Create(_ => Json("""{"value":[{"name":"refs/heads/feature-2","objectId":"abc"}]}"""));

        var result = await client.GetBranchHeadShaAsync("acme/Shop", "api", "feature");

        result.IsSuccess.Should().BeFalse();
        result.NotFound.Should().BeTrue();
    }

    [Fact]
    public async Task CreatingSendsFullRefNamesAndTheBody()
    {
        string? sent = null;
        var (client, http) = Create(request =>
        {
            sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(Pr(9), HttpStatusCode.Created);
        });

        var result = await client.CreatePullRequestAsync("acme/Shop", "api", "devpilot/task-1", "main", "Title", "Body <!-- devpilot-execution:x -->");

        result.IsSuccess.Should().BeTrue();
        http.Requests.Single().Method.Should().Be(HttpMethod.Post);
        using var doc = JsonDocument.Parse(sent!);
        doc.RootElement.GetProperty("sourceRefName").GetString().Should().Be("refs/heads/devpilot/task-1");
        doc.RootElement.GetProperty("targetRefName").GetString().Should().Be("refs/heads/main");
        doc.RootElement.GetProperty("title").GetString().Should().Be("Title");
        doc.RootElement.GetProperty("description").GetString().Should().Contain("devpilot-execution");
    }

    [Fact]
    public async Task ADescriptionOverAzuresLimitIsCut()
    {
        string? sent = null;
        var (client, _) = Create(request =>
        {
            sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(Pr(9), HttpStatusCode.Created);
        });

        await client.CreatePullRequestAsync("acme/Shop", "api", "h", "main", "T", new string('x', 9000));

        JsonDocument.Parse(sent!).RootElement.GetProperty("description").GetString()!.Length.Should().BeLessThanOrEqualTo(4000);
    }

    [Fact]
    public async Task CompletingSendsTheApprovedHeadAndWaitsForTheQueuedCompletion()
    {
        string? patch = null;
        var gets = 0;
        var (client, http) = Create(request =>
        {
            if (request.Method == HttpMethod.Patch)
            {
                patch = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(Pr(5));
            }

            return Json(++gets < 3 ? Pr(5) : Pr(5, "completed"));
        });

        var result = await client.MergePullRequestAsync("acme/Shop", "api", 5, Sha, "DevPilot Execution: abc");

        result.IsSuccess.Should().BeTrue();
        result.Data!.Merged.Should().BeTrue();
        result.Data.MergeCommitSha.Should().Be("2222222222222222222222222222222222222222");
        using var doc = JsonDocument.Parse(patch!);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");
        doc.RootElement.GetProperty("lastMergeSourceCommit").GetProperty("commitId").GetString().Should().Be(Sha);
        doc.RootElement.GetProperty("completionOptions").GetProperty("mergeStrategy").GetString().Should().Be("noFastForward");
        doc.RootElement.GetProperty("completionOptions").GetProperty("deleteSourceBranch").GetBoolean().Should().BeFalse();
        http.Requests.Count(r => r.Method == HttpMethod.Patch).Should().Be(1);
    }

    [Fact]
    public async Task ACompletionBlockedByConflictsIsReportedAsABaseConflict()
    {
        var (client, _) = Create(request => request.Method == HttpMethod.Patch ? Json(Pr(5)) : Json(Pr(5, mergeStatus: "conflicts")));

        var result = await client.MergePullRequestAsync("acme/Shop", "api", 5, Sha);

        result.IsSuccess.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.IsNotMergeable.Should().BeTrue();
        result.IsBaseConflict.Should().BeTrue();
    }

    [Fact]
    public async Task ACompletionThatNeverFinishes_IsNotReportedAsMerged_SoTheCommandCanRecoverLater()
    {
        var (client, _) = Create(request => Json(Pr(5)));

        var result = await client.MergePullRequestAsync("acme/Shop", "api", 5, Sha);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Merged.Should().BeFalse();
    }

    [Fact]
    public async Task AnAbandonedPullRequestStopsThePollingAtOnce()
    {
        var gets = 0;
        var (client, _) = Create(request =>
        {
            if (request.Method == HttpMethod.Patch)
            {
                return Json(Pr(5));
            }

            gets++;
            return Json(Pr(5, "abandoned"));
        });

        var result = await client.MergePullRequestAsync("acme/Shop", "api", 5, Sha);

        result.Data!.Merged.Should().BeFalse();
        gets.Should().Be(1);
    }

    [Theory]
    [InlineData("succeeded", "success")]
    [InlineData("failed", "failure")]
    [InlineData("error", "error")]
    [InlineData("pending", "pending")]
    [InlineData("notSet", "pending")]
    [InlineData("notApplicable", "success")]
    [InlineData("whatever", "pending")]
    public void CommitStatusesAreTranslated(string azure, string expected)
    {
        AzureDevOpsPullRequestClient.MapStatus(azure).Should().Be(expected);
    }

    [Fact]
    public async Task CiStatusesAreReadOncePerContext()
    {
        var (client, http) = Create(_ => Json("""
            {"value":[
              {"id":1,"state":"succeeded","description":"ok","context":{"genre":"continuous-integration","name":"build"},"creationDate":"2026-10-06T09:00:00Z"},
              {"id":2,"state":"failed","description":"old","context":{"genre":"continuous-integration","name":"build"},"creationDate":"2026-10-06T08:00:00Z"},
              {"id":3,"state":"pending","context":{"name":"lint"}}]}
            """));

        var result = await client.ListCommitStatusesForRefAsync("acme/Shop", "api", Sha);

        result.Data!.Select(s => (s.Context, s.State)).Should().Equal(("continuous-integration/build", "success"), ("lint", "pending"));
        http.Requests.Single().RequestUri!.AbsolutePath.Should().EndWith($"/commits/{Sha}/statuses");
        (await client.ListCheckRunsForRefAsync("acme/Shop", "api", Sha)).Data.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true, false)]
    [InlineData(HttpStatusCode.Forbidden, true, false)]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation, true, false)]
    [InlineData(HttpStatusCode.Conflict, false, true)]
    [InlineData(HttpStatusCode.BadRequest, false, true)]
    [InlineData(HttpStatusCode.InternalServerError, false, false)]
    public async Task ErrorsAreClassified(HttpStatusCode status, bool configuration, bool conflict)
    {
        var (client, _) = Create(_ => Json("{\"message\":\"TF401179: An active pull request already exists.\"}", status));

        var result = await client.CreatePullRequestAsync("acme/Shop", "api", "h", "main", "T", "B");

        result.IsSuccess.Should().BeFalse();
        result.IsConfigurationError.Should().Be(configuration);
        result.IsConflict.Should().Be(conflict);
    }

    [Fact]
    public async Task ARateLimitIsReportedAsOne()
    {
        var (client, _) = Create(_ => Json("{}", HttpStatusCode.TooManyRequests));

        (await client.GetPullRequestAsync("acme/Shop", "api", 1)).IsRateLimit.Should().BeTrue();
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("")]
    public async Task AnOwnerThatIsNotOrganizationSlashProject_IsRefusedWithoutARequest(string owner)
    {
        var (client, http) = Create(_ => Json("{}"));

        var result = await client.GetPullRequestAsync(owner, "api", 1);

        result.IsSuccess.Should().BeFalse();
        result.IsConfigurationError.Should().BeTrue();
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ATokenIsNeverIncludedInAnErrorMessage()
    {
        var (client, _) = Create(_ => Json("{\"message\":\"bad pat-123\"}", HttpStatusCode.InternalServerError));

        var result = await client.GetPullRequestAsync("acme/Shop", "api", 1);

        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    internal sealed class Http : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public Http(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }

    internal sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public Factory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    internal sealed class FixedCredentials : IGitCredentialResolver
    {
        private readonly GitCredentialResult _result;

        public FixedCredentials(string token, string username, string host) =>
            _result = GitCredentialResult.Success(new GitCredential(token, username, host));

        public Task<GitCredentialResult> ResolveAsync(GitProviderKind provider, string host, string owner, string repository, Guid? gitConnectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);

        public Task<GitCredentialResult> ResolveForWorkspaceAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);

        public Task<GitCredentialResult> ResolveForRepositoryAsync(string owner, string repository, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }
}

public class BitbucketClientTests
{
    private const string Full = "3333333333333333333333333333333333333333";
    private const string Short = "333333333333";

    private static (BitbucketPullRequestClient Client, AzureDevOpsClientTests.Http Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string username = "x-token-auth")
    {
        var handler = new AzureDevOpsClientTests.Http(respond);
        var client = new BitbucketPullRequestClient(
            new AzureDevOpsClientTests.Factory(handler),
            new AzureDevOpsClientTests.FixedCredentials("secret", username, "bitbucket.org"),
            NullLogger<BitbucketPullRequestClient>.Instance,
            TimeSpan.Zero);
        return (client, handler);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Pr(int id, string state = "OPEN", string hash = Short) =>
        JsonSerializer.Serialize(new
        {
            id,
            state,
            description = "body",
            updated_on = "2026-10-06T10:00:00.000000+00:00",
            source = new { branch = new { name = "devpilot/task-1" }, commit = new { hash } },
            destination = new { branch = new { name = "main" } },
            links = new { html = new { href = $"https://bitbucket.org/acme/api/pull-requests/{id}" } },
            merge_commit = state == "MERGED" ? new { hash = "4444444444444444444444444444444444444444" } : null,
        });

    [Fact]
    public async Task AnAccessTokenWithoutAUserNameIsSentAsABearerToken()
    {
        var (client, http) = Create(_ => Json("{\"values\":[]}"));

        await client.ListPullRequestsAsync("acme", "api", "devpilot/task-1", "main");

        var header = http.Requests.Single().Headers.Authorization!;
        header.Scheme.Should().Be("Bearer");
        header.Parameter.Should().Be("secret");
        http.Requests.Single().RequestUri!.Host.Should().Be("api.bitbucket.org");
        http.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/2.0/repositories/acme/api/pullrequests");
    }

    [Fact]
    public async Task AnAppPasswordIsSentAsBasicAuthWithTheAccountName()
    {
        var (client, http) = Create(_ => Json("{\"values\":[]}"), username: "ada");

        await client.ListPullRequestsAsync("acme", "api", "devpilot/task-1", "main");

        var header = http.Requests.Single().Headers.Authorization!;
        header.Scheme.Should().Be("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!)).Should().Be("ada:secret");
    }

    [Fact]
    public async Task ListingFiltersBySourceAndDestinationBranchAcrossStates()
    {
        var (client, http) = Create(_ => Json("{\"values\":[]}"));

        await client.ListPullRequestsAsync("acme", "api", "devpilot/task-1", "main");

        var query = Uri.UnescapeDataString(http.Requests.Single().RequestUri!.Query);
        query.Should().Contain("state=OPEN").And.Contain("state=MERGED").And.Contain("state=DECLINED");
        query.Should().Contain("source.branch.name=\"devpilot/task-1\" AND destination.branch.name=\"main\"");
    }

    [Fact]
    public async Task ABranchNameWithAQuoteCannotBreakOutOfTheQuery()
    {
        var (client, http) = Create(_ => Json("{\"values\":[]}"));

        await client.ListPullRequestsAsync("acme", "api", "x\" OR source.branch.name=\"y", "main");

        Uri.UnescapeDataString(http.Requests.Single().RequestUri!.Query).Should().Contain("x\\\" OR source.branch.name=\\\"y");
    }

    [Fact]
    public async Task TheShortCommitIdOfAPullRequestIsExpandedToTheFullOne()
    {
        var (client, http) = Create(request =>
            request.RequestUri!.AbsolutePath.Contains("/commit/") ? Json($"{{\"hash\":\"{Full}\"}}") : Json(Pr(8)));

        var pr = (await client.GetPullRequestAsync("acme", "api", 8)).Data!;

        pr.HeadSha.Should().Be(Full);
        http.Requests.Should().Contain(r => r.RequestUri!.AbsolutePath.EndsWith($"/commit/{Short}"));
    }

    [Fact]
    public async Task AFullCommitIdIsLeftAlone_WithoutAnExtraRequest()
    {
        var (client, http) = Create(_ => Json(Pr(8, hash: Full)));

        var pr = (await client.GetPullRequestAsync("acme", "api", 8)).Data!;

        pr.HeadSha.Should().Be(Full);
        http.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("OPEN", "open", false)]
    [InlineData("MERGED", "closed", true)]
    [InlineData("DECLINED", "closed", false)]
    [InlineData("SUPERSEDED", "closed", false)]
    public void PullRequestStatesAreTranslated(string state, string expected, bool merged)
    {
        using var doc = JsonDocument.Parse(Pr(1, state, Full));

        var pr = BitbucketPullRequestClient.ToPullRequest(doc.RootElement);

        pr.State.Should().Be(expected);
        pr.Merged.Should().Be(merged);
        pr.HeadRef.Should().Be("devpilot/task-1");
        pr.BaseRef.Should().Be("main");
        pr.HtmlUrl.Should().Be("https://bitbucket.org/acme/api/pull-requests/1");
        pr.ClosedAt.HasValue.Should().Be(state != "OPEN");
    }

    [Fact]
    public async Task TheBranchIsFoundByExactName_NotByPrefix()
    {
        var payload = "{\"values\":[{\"name\":\"feature-2\",\"target\":{\"hash\":\"" + new string('b', 40) + "\"}},{\"name\":\"feature\",\"target\":{\"hash\":\"" + Full + "\"}}]}";
        var (client, http) = Create(_ => Json(payload));

        var result = await client.GetBranchHeadShaAsync("acme", "api", "feature");

        result.Sha.Should().Be(Full);
        Uri.UnescapeDataString(http.Requests.Single().RequestUri!.Query).Should().Contain("name=\"feature\"");
    }

    [Fact]
    public async Task AMissingBranchIsNotFound()
    {
        var (client, _) = Create(_ => Json("{\"values\":[]}"));

        (await client.GetBranchHeadShaAsync("acme", "api", "gone")).NotFound.Should().BeTrue();
    }

    [Fact]
    public async Task CreatingSendsBranchNamesAndTheBody()
    {
        string? sent = null;
        var (client, _) = Create(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(Pr(3, hash: Full), HttpStatusCode.Created);
            }

            return Json("{}");
        });

        var result = await client.CreatePullRequestAsync("acme", "api", "devpilot/task-1", "main", "Title", "Body");

        result.IsSuccess.Should().BeTrue();
        using var doc = JsonDocument.Parse(sent!);
        doc.RootElement.GetProperty("source").GetProperty("branch").GetProperty("name").GetString().Should().Be("devpilot/task-1");
        doc.RootElement.GetProperty("destination").GetProperty("branch").GetProperty("name").GetString().Should().Be("main");
        doc.RootElement.GetProperty("close_source_branch").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AMergeThatAnswersWithTheMergedPullRequest_IsDoneAtOnce()
    {
        var (client, http) = Create(_ => Json(Pr(5, "MERGED", Full)));

        var result = await client.MergePullRequestAsync("acme", "api", 5, Full, "DevPilot Execution: abc");

        result.Data!.Merged.Should().BeTrue();
        result.Data.MergeCommitSha.Should().Be("4444444444444444444444444444444444444444");
        http.Requests.Should().ContainSingle().Which.RequestUri!.AbsolutePath.Should().EndWith("/pullrequests/5/merge");
    }

    [Fact]
    public async Task AMergeAcceptedForBackgroundWork_IsPolledUntilItFinishes()
    {
        var gets = 0;
        var (client, _) = Create(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json("{}", HttpStatusCode.Accepted);
            }

            return Json(++gets < 3 ? Pr(5, "OPEN", Full) : Pr(5, "MERGED", Full));
        });

        var result = await client.MergePullRequestAsync("acme", "api", 5, Full);

        result.Data!.Merged.Should().BeTrue();
        gets.Should().Be(3);
    }

    [Fact]
    public async Task AMergeThatNeverFinishes_IsNotReportedAsMerged()
    {
        var (client, _) = Create(request => request.Method == HttpMethod.Post ? Json("{}", HttpStatusCode.Accepted) : Json(Pr(5, "OPEN", Full)));

        var result = await client.MergePullRequestAsync("acme", "api", 5, Full);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Merged.Should().BeFalse();
    }

    [Theory]
    [InlineData("Merge conflict in src/App.tsx", true)]
    [InlineData("You need approvals from reviewers", false)]
    public async Task ARefusedMergeIsABaseConflictOnlyWhenTheMessageSaysSo(string message, bool baseConflict)
    {
        var (client, _) = Create(_ => Json($"{{\"error\":{{\"message\":\"{message}\"}}}}", HttpStatusCode.Conflict));

        var result = await client.MergePullRequestAsync("acme", "api", 5, Full);

        result.IsSuccess.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.IsNotMergeable.Should().BeTrue();
        result.IsBaseConflict.Should().Be(baseConflict);
    }

    [Theory]
    [InlineData("SUCCESSFUL", "success")]
    [InlineData("FAILED", "failure")]
    [InlineData("STOPPED", "error")]
    [InlineData("INPROGRESS", "pending")]
    [InlineData("other", "pending")]
    public void BuildStatusesAreTranslated(string bitbucket, string expected)
    {
        BitbucketPullRequestClient.MapStatus(bitbucket).Should().Be(expected);
    }

    [Fact]
    public async Task PipelineStatusesAreReadOncePerName()
    {
        var (client, _) = Create(_ => Json("""
            {"values":[
              {"key":"b1","name":"Build","state":"SUCCESSFUL","created_on":"2026-10-06T09:00:00Z"},
              {"key":"b0","name":"Build","state":"FAILED","created_on":"2026-10-06T08:00:00Z"},
              {"key":"l1","name":"Lint","state":"INPROGRESS"}]}
            """));

        var result = await client.ListCommitStatusesForRefAsync("acme", "api", Full);

        result.Data!.Select(s => (s.Context, s.State)).Should().Equal(("Build", "success"), ("Lint", "pending"));
        (await client.ListCheckRunsForRefAsync("acme", "api", Full)).Data.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true, false)]
    [InlineData(HttpStatusCode.Forbidden, true, false)]
    [InlineData(HttpStatusCode.BadRequest, false, true)]
    [InlineData(HttpStatusCode.InternalServerError, false, false)]
    public async Task ErrorsAreClassified(HttpStatusCode status, bool configuration, bool conflict)
    {
        var (client, _) = Create(_ => Json("{\"error\":{\"message\":\"nope\"}}", status));

        var result = await client.CreatePullRequestAsync("acme", "api", "h", "main", "T", "B");

        result.IsSuccess.Should().BeFalse();
        result.IsConfigurationError.Should().Be(configuration);
        result.IsConflict.Should().Be(conflict);
    }
}

public class ProviderRoutingTests : IDisposable
{
    private readonly DevPilotDbContext _db;
    private readonly AzureDevOpsClientTests.Http _http;
    private readonly RoutingPullRequestClient _router;

    public ProviderRoutingTests()
    {
        _db = new DevPilotDbContext(new DbContextOptionsBuilder<DevPilotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        // GitLab answers with a bare array, Azure DevOps and Bitbucket with an object holding one.
        _http = new AzureDevOpsClientTests.Http(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                request.RequestUri!.Host == "host.example" ? "[]" : "{\"value\":[],\"values\":[]}",
                Encoding.UTF8,
                "application/json"),
        });
        var factory = new AzureDevOpsClientTests.Factory(_http);
        var credentials = new AzureDevOpsClientTests.FixedCredentials("tok", "git", "host.example");

        _router = new RoutingPullRequestClient(
            _db,
            null!,
            new GitLabPullRequestClient(factory, credentials, NullLogger<GitLabPullRequestClient>.Instance),
            new AzureDevOpsPullRequestClient(factory, credentials, NullLogger<AzureDevOpsPullRequestClient>.Instance, TimeSpan.Zero),
            new BitbucketPullRequestClient(factory, credentials, NullLogger<BitbucketPullRequestClient>.Instance, TimeSpan.Zero));
    }

    public void Dispose() => _db.Dispose();

    private void Workspace(GitProviderKind provider, string owner, string repo)
    {
        _db.RepositoryWorkspaces.Add(new RepositoryWorkspace
        {
            Id = Guid.NewGuid(), Provider = provider, Host = "host.example", Owner = owner, Repository = repo, Branch = "main",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task AzureWorkspacesGoToTheAzureApi()
    {
        Workspace(GitProviderKind.AzureDevOps, "acme/Shop", "api");

        await _router.ListPullRequestsAsync("acme/Shop", "api", "h", "main");

        _http.Requests.Single().RequestUri!.Host.Should().Be("dev.azure.com");
    }

    [Fact]
    public async Task BitbucketWorkspacesGoToTheBitbucketApi()
    {
        Workspace(GitProviderKind.Bitbucket, "acme", "api");

        await _router.ListPullRequestsAsync("acme", "api", "h", "main");

        _http.Requests.Single().RequestUri!.Host.Should().Be("api.bitbucket.org");
    }

    [Fact]
    public async Task GitLabWorkspacesStillGoToGitLab()
    {
        Workspace(GitProviderKind.GitLab, "acme", "api");

        await _router.ListPullRequestsAsync("acme", "api", "h", "main");

        _http.Requests.Single().RequestUri!.Host.Should().Be("host.example");
    }

    [Fact]
    public async Task GenericWorkspacesNeverReachAnyApi()
    {
        Workspace(GitProviderKind.Generic, "acme", "api");

        var result = await _router.ListPullRequestsAsync("acme", "api", "h", "main");

        result.IsSuccess.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        _http.Requests.Should().BeEmpty();
    }
}
