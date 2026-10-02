using DevPilot.Application.AiProviders;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.AiProviders;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DevPilot.Tests.AiModels;

public class ModelDiscoveryTests
{
    private static DevPilotDbContext NewDb() =>
        new(new DbContextOptionsBuilder<DevPilotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AiModelDiscoveryService NewService(DevPilotDbContext db) =>
        new(db, new AiModelRoutingTests.FakeProtector(), new PlainClientFactory());

    private static FakeHttpServer.Reply Json(string body, int status = 200) => new(status, body, "application/json");

    [Fact]
    public async Task OpenAiCompatible_ListsSortedIds_WithBearerKey()
    {
        using var server = new FakeHttpServer(Json("""{"data":[{"id":"zeta"},{"id":"Alpha","name":"Alpha Model"},{"id":"mid"}]}"""));

        var result = await NewService(NewDb()).DiscoverAsync(
            new DiscoverAiModelsRequest { BaseUrl = server.BaseUrl, ApiKey = "sk-live" }, CancellationToken.None);

        result.Success.Should().BeTrue(result.Message);
        result.Models.Select(m => m.Id).Should().Equal("Alpha", "mid", "zeta");
        result.Models[0].DisplayName.Should().Be("Alpha Model");

        var sent = server.Requests.Single();
        sent.RequestLine.Should().StartWith("GET /v1/models");
        sent.Headers["Authorization"].Should().Be("Bearer sk-live");
    }

    [Fact]
    public async Task OpenAiCompatible_DoesNotDuplicateTheVersionSegment()
    {
        using var server = new FakeHttpServer(Json("""{"data":[{"id":"m"}]}"""));

        await NewService(NewDb()).DiscoverAsync(
            new DiscoverAiModelsRequest { BaseUrl = server.BaseUrl + "/v1" }, CancellationToken.None);

        server.Requests.Single().RequestLine.Should().StartWith("GET /v1/models").And.NotContain("/v1/v1");
    }

    [Fact]
    public async Task Gemini_KeepsOnlyModelsThatCanGenerate_AndStripsThePrefix()
    {
        using var server = new FakeHttpServer(Json("""
            {"models":[
              {"name":"models/gemini-flash","displayName":"Gemini Flash","supportedGenerationMethods":["generateContent","countTokens"]},
              {"name":"models/text-embedding","displayName":"Embedding","supportedGenerationMethods":["embedContent"]}
            ]}
            """));

        var result = await NewService(NewDb()).DiscoverAsync(
            new DiscoverAiModelsRequest { AdapterType = AiAdapterType.Gemini, BaseUrl = server.BaseUrl, ApiKey = "g-key" },
            CancellationToken.None);

        result.Models.Should().ContainSingle().Which.Id.Should().Be("gemini-flash");
        var sent = server.Requests.Single();
        sent.RequestLine.Should().StartWith("GET /v1beta/models");
        sent.Headers["x-goog-api-key"].Should().Be("g-key");
    }

    [Fact]
    public async Task Claude_UsesAnthropicHeaders_AndReadsDisplayName()
    {
        using var server = new FakeHttpServer(Json("""{"data":[{"id":"claude-x","display_name":"Claude X"}]}"""));

        var result = await NewService(NewDb()).DiscoverAsync(
            new DiscoverAiModelsRequest { AdapterType = AiAdapterType.Claude, BaseUrl = server.BaseUrl, ApiKey = "sk-ant" },
            CancellationToken.None);

        result.Models.Single().DisplayName.Should().Be("Claude X");
        var sent = server.Requests.Single();
        sent.Headers["x-api-key"].Should().Be("sk-ant");
        sent.Headers["anthropic-version"].Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task EditingAModel_ReusesItsStoredKey_WithoutTheBrowserSendingIt()
    {
        using var server = new FakeHttpServer(Json("""{"data":[{"id":"m"}]}"""));
        await using var db = NewDb();
        var stored = new AiModelConfig
        {
            Id = Guid.NewGuid(), Name = "x", ModelName = "m", BaseUrl = server.BaseUrl,
            ProtectedApiKey = AiModelRoutingTests.FakeProtector.Protected("stored-key"),
        };
        db.AiModelConfigs.Add(stored);
        await db.SaveChangesAsync();

        var result = await NewService(db).DiscoverAsync(
            new DiscoverAiModelsRequest { BaseUrl = server.BaseUrl, ExistingModelId = stored.Id }, CancellationToken.None);

        result.Success.Should().BeTrue(result.Message);
        server.Requests.Single().Headers["Authorization"].Should().Be("Bearer stored-key");
    }

    [Fact]
    public async Task StoredKey_IsNeverSentToADifferentHost()
    {
        using var original = new FakeHttpServer(Json("""{"data":[]}"""));
        using var other = new FakeHttpServer(Json("""{"data":[{"id":"m"}]}"""));
        await using var db = NewDb();
        var stored = new AiModelConfig
        {
            Id = Guid.NewGuid(), Name = "x", ModelName = "m", BaseUrl = original.BaseUrl,
            ProtectedApiKey = AiModelRoutingTests.FakeProtector.Protected("stored-key"),
        };
        db.AiModelConfigs.Add(stored);
        await db.SaveChangesAsync();

        var act = () => NewService(db).DiscoverAsync(
            new DiscoverAiModelsRequest { BaseUrl = other.BaseUrl, ExistingModelId = stored.Id }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        other.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoteEndpointWithoutKey_AsksForTheKey_InsteadOfCallingOut()
    {
        var result = await NewService(NewDb()).DiscoverAsync(
            new DiscoverAiModelsRequest { BaseUrl = "https://api.example.com" }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("API key");
    }

    [Fact]
    public async Task LocalRuntime_WorksWithoutAKey()
    {
        using var server = new FakeHttpServer(Json("""{"data":[{"id":"llama3"}]}"""));

        var result = await NewService(NewDb()).DiscoverAsync(
            new DiscoverAiModelsRequest { BaseUrl = server.BaseUrl }, CancellationToken.None);

        result.Success.Should().BeTrue();
        server.Requests.Single().Headers.Should().NotContainKey("Authorization");
    }

    [Fact]
    public async Task ProviderErrors_AreReportedWithTheProvidersMessage()
    {
        using var server = new FakeHttpServer(Json("""{"error":{"message":"API key not valid"}}""", status: 401));

        var result = await NewService(NewDb()).DiscoverAsync(
            new DiscoverAiModelsRequest { BaseUrl = server.BaseUrl, ApiKey = "bad" }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("401");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://x.example")]
    public async Task InvalidBaseUrl_IsRejected(string url)
    {
        var act = () => NewService(NewDb()).DiscoverAsync(new DiscoverAiModelsRequest { BaseUrl = url }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class PlainClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
