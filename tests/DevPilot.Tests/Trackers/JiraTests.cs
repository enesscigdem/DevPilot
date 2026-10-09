using System.Net;
using System.Text;
using System.Text.Json;
using DevPilot.Api.Controllers;
using DevPilot.Application.Tasks.Commands.CreateTask;
using DevPilot.Application.Tasks.Dtos;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Application.Trackers;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.Trackers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Trackers;

public class JiraQueryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoQuery_ListsTheOpenIssuesAssignedToTheAccount(string? query)
    {
        JiraQuery.ToJql(query).Should().Be(JiraQuery.DefaultJql);
    }

    [Theory]
    [InlineData("ARF-123", "key = \"ARF-123\"")]
    [InlineData("arf-9", "key = \"ARF-9\"")]
    [InlineData("  PROJ_X-42 ", "key = \"PROJ_X-42\"")]
    public void AnIssueKey_IsLookedUpDirectly(string query, string expected)
    {
        JiraQuery.ToJql(query).Should().Be(expected);
    }

    [Theory]
    [InlineData("project = ARF AND status = \"To Do\"")]
    [InlineData("assignee = currentUser() ORDER BY created")]
    [InlineData("labels in (devpilot, ai)")]
    [InlineData("summary ~ login")]
    [InlineData("priority != Low")]
    [InlineData("duedate < now()")]
    [InlineData("assignee is EMPTY")]
    [InlineData("status not in (Done)")]
    public void AJqlClause_IsUsedAsIs(string query)
    {
        JiraQuery.ToJql(query).Should().Be(query);
    }

    [Theory]
    [InlineData("login is broken", "text ~ \"login is broken\" ORDER BY updated DESC")]
    [InlineData("search results", "text ~ \"search results\" ORDER BY updated DESC")]
    [InlineData("say \"hi\"", "text ~ \"say \\\"hi\\\"\" ORDER BY updated DESC")]
    public void PlainWords_AreSearchedAsText_EvenWhenTheyContainJqlKeywords(string query, string expected)
    {
        JiraQuery.ToJql(query).Should().Be(expected);
    }

    [Theory]
    [InlineData("ARF-1", true)]
    [InlineData("a-1", true)]
    [InlineData("ARF", false)]
    [InlineData("ARF-", false)]
    [InlineData("1-ARF", false)]
    [InlineData("ARF-1; DROP", false)]
    [InlineData("../ARF-1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IssueKeys_AreRecognisedStrictly(string? value, bool expected)
    {
        JiraQuery.IsIssueKey(value).Should().Be(expected);
    }
}

public class JiraTrackerClientTests
{
    private static readonly TrackerConnectionInfo Cloud =
        new(Guid.NewGuid(), TrackerProviderKind.Jira, "https://acme.atlassian.net", "me@acme.com", "api-token");

    private static readonly TrackerConnectionInfo Server =
        new(Guid.NewGuid(), TrackerProviderKind.Jira, "https://jira.acme.local/jira", null, "pat-token");

    private const string IssueJson = """
        {"key":"ARF-7","fields":{"summary":"Add dark mode","description":"Users want a dark theme.",
         "status":{"name":"To Do"},"issuetype":{"name":"Story"},"priority":{"name":"High"},"labels":["ui","theme"]}}
        """;

    private static (JiraTrackerClient Client, Recorder Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Recorder(respond);
        return (new JiraTrackerClient(new SingleClientFactory(handler), NullLogger<JiraTrackerClient>.Instance), handler);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Cloud_UsesBasicAuthWithEmailAndToken()
    {
        var (client, handler) = Create(_ => Json("{\"displayName\":\"Ada\"}"));

        await client.ValidateAsync(Cloud);

        var header = handler.Requests.Single().Headers.Authorization!;
        header.Scheme.Should().Be("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!)).Should().Be("me@acme.com:api-token");
    }

    [Fact]
    public async Task ServerAndDataCenter_UseABearerToken()
    {
        var (client, handler) = Create(_ => Json("{\"displayName\":\"Ada\"}"));

        await client.ValidateAsync(Server);

        var header = handler.Requests.Single().Headers.Authorization!;
        header.Scheme.Should().Be("Bearer");
        header.Parameter.Should().Be("pat-token");
        handler.Requests.Single().RequestUri!.ToString().Should().Be("https://jira.acme.local/jira/rest/api/2/myself");
    }

    [Fact]
    public async Task Validate_ReturnsTheAccountName()
    {
        var (client, _) = Create(_ => Json("{\"displayName\":\"Ada Lovelace\"}"));

        var result = await client.ValidateAsync(Cloud);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be("Ada Lovelace");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task Failures_AreReportedWithAReadableMessage_AndTheTokenNeverLeaksIntoIt(HttpStatusCode status, bool isAuth)
    {
        var (client, _) = Create(_ => Json("{\"message\":\"api-token is wrong\"}", status));

        var result = await client.ValidateAsync(Cloud);

        result.IsSuccess.Should().BeFalse();
        result.IsAuthError.Should().Be(isAuth);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace().And.NotContain("api-token");
    }

    [Fact]
    public async Task ABadQuery_ShowsJirasOwnExplanation()
    {
        var (client, _) = Create(_ => Json("{\"errorMessages\":[\"Field 'foo' does not exist.\"]}", HttpStatusCode.BadRequest));

        var result = await client.SearchAsync(Cloud, "foo = 1");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Field 'foo' does not exist.");
    }

    [Fact]
    public async Task Cloud_SearchesThroughTheJqlEndpoint()
    {
        var (client, handler) = Create(_ => Json("{\"issues\":[]}"));

        await client.SearchAsync(Cloud, "project = ARF");

        var uri = handler.Requests.Single().RequestUri!;
        uri.AbsolutePath.Should().Be("/rest/api/2/search/jql");
        Uri.UnescapeDataString(uri.Query).Should().Contain("jql=project = ARF").And.Contain("maxResults=50");
    }

    [Fact]
    public async Task Server_SearchesThroughTheClassicEndpoint()
    {
        var (client, handler) = Create(_ => Json("{\"issues\":[]}"));

        await client.SearchAsync(Server, "project = ARF");

        handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/jira/rest/api/2/search");
    }

    [Fact]
    public async Task Search_ParsesIssuesIntoTasks_AndSkipsMalformedOnes()
    {
        var payload = "{\"issues\":[" + IssueJson + ",{\"key\":\"NO-FIELDS\"},{\"fields\":{}}]}";
        var (client, _) = Create(_ => Json(payload));

        var result = await client.SearchAsync(Cloud, null);

        var issue = result.Data!.Should().ContainSingle().Subject;
        issue.Key.Should().Be("ARF-7");
        issue.Summary.Should().Be("Add dark mode");
        issue.Description.Should().Be("Users want a dark theme.");
        issue.Status.Should().Be("To Do");
        issue.IssueType.Should().Be("Story");
        issue.Priority.Should().Be("High");
        issue.Labels.Should().Equal("ui", "theme");
        issue.Url.Should().Be("https://acme.atlassian.net/browse/ARF-7");
    }

    [Fact]
    public async Task ADescriptionInAtlassianDocumentFormat_IsFlattenedToText()
    {
        const string adf = """
            {"key":"ARF-8","fields":{"summary":"S","description":{"type":"doc","content":[
              {"type":"paragraph","content":[{"type":"text","text":"First line."}]},
              {"type":"paragraph","content":[{"type":"text","text":"Second "},{"type":"text","text":"line."}]}]}}}
            """;
        var (client, _) = Create(_ => Json(adf));

        var result = await client.GetIssueAsync(Cloud, "ARF-8");

        result.Data!.Description.Should().Contain("First line.").And.Contain("Second line.");
    }

    [Fact]
    public async Task AnIssueWithoutADescription_IsStillReadable()
    {
        var (client, _) = Create(_ => Json("{\"key\":\"ARF-9\",\"fields\":{\"summary\":\"No body\",\"description\":null}}"));

        var result = await client.GetIssueAsync(Cloud, "ARF-9");

        result.IsSuccess.Should().BeTrue();
        result.Data!.Description.Should().BeNull();
    }

    [Fact]
    public async Task AnUnknownIssue_SaysSoInPlainWords()
    {
        var (client, _) = Create(_ => Json("{}", HttpStatusCode.NotFound));

        var result = await client.GetIssueAsync(Cloud, "ARF-404");

        result.IsNotFound.Should().BeTrue();
        result.ErrorMessage.Should().Contain("ARF-404").And.Contain("not found");
    }

    [Theory]
    [InlineData("not a key")]
    [InlineData("../../etc")]
    [InlineData("ARF-1/comment")]
    public async Task ARequestWithAMalformedKey_IsNeverSent(string key)
    {
        var (client, handler) = Create(_ => Json("{}"));

        var read = await client.GetIssueAsync(Cloud, key);
        var comment = await client.AddCommentAsync(Cloud, key, "hello");

        read.IsSuccess.Should().BeFalse();
        comment.IsSuccess.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AddComment_PostsTheTextToTheIssue()
    {
        string? body = null;
        var (client, handler) = Create(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("{\"id\":\"1\"}", HttpStatusCode.Created);
        });

        var result = await client.AddCommentAsync(Cloud, "ARF-7", "DevPilot opened a pull request: #22");

        result.IsSuccess.Should().BeTrue();
        handler.Requests.Single().Method.Should().Be(HttpMethod.Post);
        handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/rest/api/2/issue/ARF-7/comment");
        JsonDocument.Parse(body!).RootElement.GetProperty("body").GetString().Should().Be("DevPilot opened a pull request: #22");
    }

    [Fact]
    public async Task ANetworkFailure_IsReportedNotThrown()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("name resolution failed"));

        var result = await client.ValidateAsync(Cloud);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Could not reach Jira");
    }

    [Fact]
    public async Task AnInvalidSiteAddress_IsRejectedWithoutARequest()
    {
        var (client, handler) = Create(_ => Json("{}"));

        var result = await client.ValidateAsync(Cloud with { BaseUrl = "not a url" });

        result.IsSuccess.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://acme.atlassian.net", true)]
    [InlineData("https://ACME.Atlassian.NET", true)]
    [InlineData("https://jira.acme.local", false)]
    [InlineData("https://atlassian.net.evil.com", false)]
    public void OnlyAtlassianCloudHostsUseTheCloudSearchEndpoint(string url, bool cloud)
    {
        JiraTrackerClient.IsCloud(url).Should().Be(cloud);
    }

    private sealed class Recorder : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public Recorder(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}

public class ImportTrackerIssuesTests
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly FakeConnections _connections = new();
    private readonly FakeTracker _tracker = new();
    private readonly FakeTasks _tasks = new();
    private readonly FakeCreate _create = new();
    private readonly ImportTrackerIssuesCommandHandler _handler;

    public ImportTrackerIssuesTests()
    {
        _handler = new ImportTrackerIssuesCommandHandler(
            _connections, _tracker, _tasks, _create, NullLogger<ImportTrackerIssuesCommandHandler>.Instance);
    }

    private ImportTrackerIssuesCommand Command(params string[] keys) =>
        new(_connections.Info.Id, _workspaceId, keys);

    [Fact]
    public async Task ImportsEachIssueThroughTheNormalCreateHandler_KeepingTheLinkBack()
    {
        _tracker.Add("ARF-1", "Add dark mode", "Details", "High");

        var result = await _handler.HandleAsync(Command("ARF-1"));

        result.Success.Should().BeTrue();
        var item = result.Items.Should().ContainSingle().Subject;
        item.Outcome.Should().Be(ImportOutcome.Imported);
        var dto = _create.Created.Should().ContainSingle().Subject;
        dto.RepositoryWorkspaceId.Should().Be(_workspaceId);
        dto.Title.Should().Be("Add dark mode");
        dto.Description.Should().Be("Details");
        dto.Priority.Should().Be(DevelopmentTaskPriority.High);
        dto.ExternalSource.Should().Be("Jira");
        dto.ExternalKey.Should().Be("ARF-1");
        dto.ExternalUrl.Should().Be("https://acme.atlassian.net/browse/ARF-1");
        dto.ExternalConnectionId.Should().Be(_connections.Info.Id);
    }


    [Fact]
    public async Task TheSameKeyFromAnotherJiraSite_IsADifferentIssueAndIsImported()
    {
        _tracker.Add("APP-42", "From the second site", null, null);
        _tasks.Existing.Add("APP-42");
        _tasks.ExistingOrigin = "https://other-company.atlassian.net";

        var result = await _handler.HandleAsync(Command("APP-42"));

        result.Items.Single().Outcome.Should().Be(ImportOutcome.Imported);
        _create.Created.Single().ExternalOrigin.Should().Be("https://acme.atlassian.net");
    }

    [Fact]
    public async Task TheSameIssueAfterTheConnectionWasRecreated_IsStillRecognised()
    {
        _tracker.Add("APP-42", "Already here", null, null);
        _tasks.Existing.Add("APP-42");
        _connections.Info = new TrackerConnectionInfo(Guid.NewGuid(), TrackerProviderKind.Jira, "https://ACME.atlassian.net/", "me@acme.com", "new-token");

        var result = await _handler.HandleAsync(Command("APP-42"));

        result.Items.Single().Outcome.Should().Be(ImportOutcome.AlreadyImported);
    }

    [Theory]
    [InlineData("https://acme.atlassian.net", "https://acme.atlassian.net")]
    [InlineData("  https://ACME.atlassian.net/  ", "https://acme.atlassian.net")]
    [InlineData("https://jira.company.com/jira/", "https://jira.company.com/jira")]
    [InlineData(null, "")]
    public void TheOriginIsTheNormalisedSiteAddress(string? baseUrl, string expected) =>
        TrackerOrigin.Normalize(baseUrl).Should().Be(expected);
    [Fact]
    public async Task AnIssueThatIsAlreadyATask_IsSkippedAndNeverFetched()
    {
        _tracker.Add("ARF-1", "A", null, null);
        _tracker.Add("ARF-2", "B", null, null);
        _tasks.Existing.Add("ARF-1");

        var result = await _handler.HandleAsync(Command("ARF-1", "ARF-2"));

        result.Items.Select(i => (i.Key, i.Outcome)).Should().Equal(
            ("ARF-1", ImportOutcome.AlreadyImported),
            ("ARF-2", ImportOutcome.Imported));
        _tracker.Fetched.Should().Equal("ARF-2");
        _create.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task OneFailingIssue_DoesNotStopTheOthers()
    {
        _tracker.Add("ARF-1", "A", null, null);
        _tracker.Add("ARF-3", "C", null, null);

        var result = await _handler.HandleAsync(Command("ARF-1", "ARF-2", "ARF-3"));

        result.Items.Select(i => i.Outcome).Should().Equal(ImportOutcome.Imported, ImportOutcome.Failed, ImportOutcome.Imported);
        result.Items[1].Message.Should().Contain("ARF-2");
    }

    [Fact]
    public async Task ATaskTheCreateHandlerRefuses_IsReportedWithItsReason()
    {
        _tracker.Add("ARF-1", "A", null, null);
        _create.RefuseWith = "Title is required.";

        var result = await _handler.HandleAsync(Command("ARF-1"));

        result.Items.Single().Outcome.Should().Be(ImportOutcome.Failed);
        result.Items.Single().Message.Should().Be("Title is required.");
    }

    [Fact]
    public async Task KeysAreNormalisedAndDeduplicated()
    {
        _tracker.Add("ARF-1", "A", null, null);

        var result = await _handler.HandleAsync(Command(" arf-1 ", "ARF-1", "", "  "));

        result.Items.Should().ContainSingle();
        _create.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task NothingSelected_IsRejected()
    {
        var result = await _handler.HandleAsync(Command("", " "));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("at least one");
    }

    [Fact]
    public async Task MoreThanTheLimit_IsRejectedBeforeAnythingIsFetched()
    {
        var keys = Enumerable.Range(1, ImportTrackerIssuesCommandHandler.MaxIssuesPerImport + 1).Select(i => $"ARF-{i}").ToArray();

        var result = await _handler.HandleAsync(Command(keys));

        result.Success.Should().BeFalse();
        _tracker.Fetched.Should().BeEmpty();
    }

    [Fact]
    public async Task AMissingConnection_IsRejectedWithAClearHint()
    {
        _connections.Info = null;

        var result = await _handler.HandleAsync(new ImportTrackerIssuesCommand(Guid.NewGuid(), _workspaceId, new[] { "ARF-1" }));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("connection");
    }

    [Fact]
    public async Task LongTextIsCutToTheLimitsTasksAccept()
    {
        _tracker.Add("ARF-1", new string('t', 500), new string('d', 20000), null);

        await _handler.HandleAsync(Command("ARF-1"));

        var dto = _create.Created.Single();
        dto.Title.Length.Should().BeLessThanOrEqualTo(200);
        dto.Description.Length.Should().BeLessThanOrEqualTo(10000);
    }

    [Theory]
    [InlineData("Highest", DevelopmentTaskPriority.Critical)]
    [InlineData("Blocker", DevelopmentTaskPriority.Critical)]
    [InlineData("High", DevelopmentTaskPriority.High)]
    [InlineData("Major", DevelopmentTaskPriority.High)]
    [InlineData("Medium", DevelopmentTaskPriority.Medium)]
    [InlineData("Normal", DevelopmentTaskPriority.Medium)]
    [InlineData(null, DevelopmentTaskPriority.Medium)]
    [InlineData("Low", DevelopmentTaskPriority.Low)]
    [InlineData("Trivial", DevelopmentTaskPriority.Low)]
    public void PrioritiesAreMapped(string? jira, DevelopmentTaskPriority expected)
    {
        ImportTrackerIssuesCommandHandler.MapPriority(jira).Should().Be(expected);
    }

    private sealed class FakeConnections : ITrackerConnectionStore
    {
        public TrackerConnectionInfo? Info { get; set; } =
            new(Guid.NewGuid(), TrackerProviderKind.Jira, "https://acme.atlassian.net", "me@acme.com", "t");

        public Task<IReadOnlyList<TrackerConnectionDto>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TrackerConnectionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TrackerConnectionInfo?> GetInfoAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Info);

        public Task<TrackerConnectionDto> CreateAsync(TrackerProviderKind provider, string baseUrl, string displayName, string? email, string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeTracker : ITrackerClient
    {
        private readonly Dictionary<string, TrackerIssue> _issues = new();

        public List<string> Fetched { get; } = new();

        public void Add(string key, string summary, string? description, string? priority) =>
            _issues[key] = new TrackerIssue(key, summary, description, "To Do", "Story", priority, Array.Empty<string>(), $"https://acme.atlassian.net/browse/{key}");

        public Task<TrackerResult<TrackerIssue>> GetIssueAsync(TrackerConnectionInfo connection, string key, CancellationToken cancellationToken = default)
        {
            Fetched.Add(key);
            return Task.FromResult(_issues.TryGetValue(key, out var issue)
                ? TrackerResult<TrackerIssue>.Success(issue)
                : TrackerResult<TrackerIssue>.Failure($"Issue {key} was not found.", isNotFound: true));
        }

        public Task<TrackerResult<string>> ValidateAsync(TrackerConnectionInfo connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TrackerResult<IReadOnlyList<TrackerIssue>>> SearchAsync(TrackerConnectionInfo connection, string? query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TrackerResult<bool>> AddCommentAsync(TrackerConnectionInfo connection, string key, string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeTasks : ITaskRepository
    {
        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The site the keys in <see cref="Existing"/> were imported from.</summary>
        public string ExistingOrigin { get; set; } = "https://acme.atlassian.net";

        public Task<IReadOnlySet<string>> FindByExternalKeysAsync(Guid repositoryWorkspaceId, string externalSource, string externalOrigin, IReadOnlyCollection<string> externalKeys, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(
                externalOrigin == TrackerOrigin.Normalize(ExistingOrigin)
                    ? externalKeys.Where(Existing.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>());

        public Task AddAsync(DevelopmentTask task, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateAsync(DevelopmentTask task, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(DevelopmentTask task, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DevelopmentTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<DevelopmentTask>> GetAllAsync(DevelopmentTaskQueryFilter filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeCreate : ICreateTaskCommandHandler
    {
        public List<CreateTaskDto> Created { get; } = new();

        public string? RefuseWith { get; set; }

        public Task<CreateTaskResult> HandleAsync(CreateTaskCommand command, CancellationToken cancellationToken = default)
        {
            if (RefuseWith is not null)
            {
                return Task.FromResult(new CreateTaskResult { Success = false, ErrorMessage = RefuseWith });
            }

            Created.Add(command.Dto);
            return Task.FromResult(new CreateTaskResult { Success = true, Task = new TaskDto { Id = Guid.NewGuid(), Title = command.Dto.Title } });
        }
    }
}

public class TrackerStoreAndNotifierTests : IDisposable
{
    private readonly DevPilotDbContext _db;
    private readonly PlainProtector _protector = new();
    private readonly EfTrackerConnectionStore _store;
    private readonly RecordingClient _client = new();
    private readonly TaskExternalNotifier _notifier;

    public TrackerStoreAndNotifierTests()
    {
        _db = new DevPilotDbContext(new DbContextOptionsBuilder<DevPilotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _store = new EfTrackerConnectionStore(_db, _protector);
        _notifier = new TaskExternalNotifier(_db, _store, _client, NullLogger<TaskExternalNotifier>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task<DevelopmentTask> AddTaskAsync(string? key, Guid? connectionId)
    {
        var workspace = new RepositoryWorkspace { Id = Guid.NewGuid(), Owner = "o", Repository = "r", Branch = "main" };
        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(), RepositoryWorkspaceId = workspace.Id, Title = "t", Status = DevelopmentTaskStatus.Draft,
            ExternalSource = key is null ? null : "Jira", ExternalKey = key, ExternalConnectionId = connectionId,
        };
        _db.RepositoryWorkspaces.Add(workspace);
        _db.DevelopmentTasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    [Fact]
    public async Task TheTokenIsStoredEncrypted_AndNeverReturnedInTheDto()
    {
        var dto = await _store.CreateAsync(TrackerProviderKind.Jira, "https://acme.atlassian.net/", "Acme", " me@acme.com ", " secret-token ");

        var row = await _db.TrackerConnections.SingleAsync();
        row.EncryptedToken.Should().NotContain("secret-token").And.StartWith("enc:");
        row.BaseUrl.Should().Be("https://acme.atlassian.net");
        row.Email.Should().Be("me@acme.com");
        JsonSerializer.Serialize(dto).Should().NotContain("secret-token");
        (await _store.GetInfoAsync(dto.Id))!.Token.Should().Be("secret-token");
    }

    [Fact]
    public async Task AnUnreadableToken_YieldsNoConnectionInfo()
    {
        var dto = await _store.CreateAsync(TrackerProviderKind.Jira, "https://acme.atlassian.net", "Acme", null, "t");
        _protector.CanDecrypt = false;

        (await _store.GetInfoAsync(dto.Id)).Should().BeNull();
        (await _store.GetAsync(dto.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeletingAConnection_KeepsTheImportedTasksButStopsReportingBack()
    {
        var connection = await _store.CreateAsync(TrackerProviderKind.Jira, "https://acme.atlassian.net", "Acme", null, "t");
        var task = await AddTaskAsync("ARF-1", connection.Id);

        (await _store.DeleteAsync(connection.Id)).Should().BeTrue();

        var reloaded = await _db.DevelopmentTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        reloaded.ExternalKey.Should().Be("ARF-1");
        reloaded.ExternalConnectionId.Should().BeNull();
        (await _store.DeleteAsync(connection.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task ALinkedTask_GetsACommentOnItsIssue()
    {
        var connection = await _store.CreateAsync(TrackerProviderKind.Jira, "https://acme.atlassian.net", "Acme", "me@acme.com", "tok");
        var task = await AddTaskAsync("ARF-1", connection.Id);

        await _notifier.NotifyAsync(task.Id, "DevPilot opened a pull request");

        var call = _client.Comments.Should().ContainSingle().Subject;
        call.Key.Should().Be("ARF-1");
        call.Text.Should().Be("DevPilot opened a pull request");
        call.Connection.Token.Should().Be("tok");
    }

    [Fact]
    public async Task ATaskNotImportedFromJira_IsLeftAlone()
    {
        var plain = await AddTaskAsync(null, null);

        await _notifier.NotifyAsync(plain.Id, "x");
        await _notifier.NotifyAsync(Guid.NewGuid(), "x");

        _client.Comments.Should().BeEmpty();
    }

    [Fact]
    public async Task ATaskWhoseConnectionWasRemoved_IsLeftAlone()
    {
        var task = await AddTaskAsync("ARF-1", null);

        await _notifier.NotifyAsync(task.Id, "x");

        _client.Comments.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailingJira_NeverBreaksTheCaller()
    {
        var connection = await _store.CreateAsync(TrackerProviderKind.Jira, "https://acme.atlassian.net", "Acme", null, "t");
        var task = await AddTaskAsync("ARF-1", connection.Id);
        _client.Throw = true;

        var act = () => _notifier.NotifyAsync(task.Id, "x");
        await act.Should().NotThrowAsync();

        _client.Throw = false;
        _client.Fail = true;
        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("acme.atlassian.net", "https://acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net/", "https://acme.atlassian.net")]
    [InlineData("  https://jira.acme.local:8443/jira/  ", "https://jira.acme.local:8443/jira")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    public void SiteAddressesAreNormalised(string raw, string expected)
    {
        TrackerConnectionsController.NormalizeBaseUrl(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net/?x=1")]
    [InlineData("https://acme.atlassian.net/#frag")]
    public void InvalidSiteAddressesAreRejected(string? raw)
    {
        TrackerConnectionsController.NormalizeBaseUrl(raw).Should().BeNull();
    }

    private sealed class PlainProtector : ITrackerSecretProtector
    {
        public bool CanDecrypt { get; set; } = true;

        public string Protect(string plainText) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string? Unprotect(string protectedText) =>
            CanDecrypt && protectedText.StartsWith("enc:") ? Encoding.UTF8.GetString(Convert.FromBase64String(protectedText[4..])) : null;
    }

    private sealed class RecordingClient : ITrackerClient
    {
        public List<(TrackerConnectionInfo Connection, string Key, string Text)> Comments { get; } = new();

        public bool Throw { get; set; }

        public bool Fail { get; set; }

        public Task<TrackerResult<bool>> AddCommentAsync(TrackerConnectionInfo connection, string key, string text, CancellationToken cancellationToken = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("jira is down");
            }

            if (Fail)
            {
                return Task.FromResult(TrackerResult<bool>.Failure("HTTP 500"));
            }

            Comments.Add((connection, key, text));
            return Task.FromResult(TrackerResult<bool>.Success(true));
        }

        public Task<TrackerResult<string>> ValidateAsync(TrackerConnectionInfo connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TrackerResult<IReadOnlyList<TrackerIssue>>> SearchAsync(TrackerConnectionInfo connection, string? query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TrackerResult<TrackerIssue>> GetIssueAsync(TrackerConnectionInfo connection, string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
