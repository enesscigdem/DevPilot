using System.Net;
using System.Text;
using DevPilot.Application.AiProviders;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.AiProviders;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.AiModels;

public class AiModelRoutingTests
{
    private static DevPilotDbContext NewDb() =>
        new(new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static (RoutingAiProvider Router, FakeFactory Factory, RecordingProvider Legacy) NewRouter(
        DevPilotDbContext db,
        IAiExecutionContext? executionContext = null)
    {
        var factory = new FakeFactory();
        var legacy = new RecordingProvider("legacy");
        var router = new RoutingAiProvider(db, new FakeProtector(), factory, legacy, executionContext);
        return (router, factory, legacy);
    }

    private static AiModelConfig Model(string name, bool isDefault = false, bool enabled = true, string key = "sk-test-1234", int? maxTokens = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            AdapterType = AiAdapterType.OpenAiCompatible,
            BaseUrl = "https://api.example.com",
            ModelName = $"{name}-model",
            ProtectedApiKey = FakeProtector.Protected(key),
            IsDefault = isDefault,
            IsEnabled = enabled,
            MaxOutputTokens = maxTokens,
        };

    [Fact]
    public async Task NoModelsConfigured_FallsBackToLegacyProvider()
    {
        await using var db = NewDb();
        var (router, factory, legacy) = NewRouter(db);

        var response = await router.SendAsync(new AiRequest { UserPrompt = "hi", Stage = AiStage.Planning });

        response.Provider.Should().Be("legacy");
        legacy.Calls.Should().Be(1);
        factory.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task StageAssignment_WinsOverDefault_AndOverridesRequestModel()
    {
        await using var db = NewDb();
        var fallback = Model("fallback", isDefault: true);
        var coder = Model("coder");
        db.AiModelConfigs.AddRange(fallback, coder);
        db.AiStageAssignments.Add(new AiStageAssignment { Id = Guid.NewGuid(), Stage = AiStage.CodeGeneration, AiModelConfigId = coder.Id });
        await db.SaveChangesAsync();
        var (router, factory, _) = NewRouter(db);

        var response = await router.SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.CodeGeneration, Model = "caller-chose-this" });

        response.Provider.Should().Be("coder");
        factory.Last!.Request!.Model.Should().Be("coder-model");
    }

    [Fact]
    public async Task UnassignedStage_UsesDefaultModel()
    {
        await using var db = NewDb();
        db.AiModelConfigs.AddRange(Model("main", isDefault: true), Model("other"));
        await db.SaveChangesAsync();
        var (router, _, _) = NewRouter(db);

        var response = await router.SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.Brain });

        response.Provider.Should().Be("main");
    }

    [Fact]
    public async Task DisabledAssignedModel_IsIgnored()
    {
        await using var db = NewDb();
        var main = Model("main", isDefault: true);
        var off = Model("off", enabled: false);
        db.AiModelConfigs.AddRange(main, off);
        db.AiStageAssignments.Add(new AiStageAssignment { Id = Guid.NewGuid(), Stage = AiStage.Repair, AiModelConfigId = off.Id });
        await db.SaveChangesAsync();
        var (router, _, _) = NewRouter(db);

        var response = await router.SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.Repair });

        response.Provider.Should().Be("main");
    }

    [Fact]
    public async Task SingleModelWithoutDefaultFlag_IsStillUsed()
    {
        await using var db = NewDb();
        db.AiModelConfigs.Add(Model("only"));
        await db.SaveChangesAsync();
        var (router, _, legacy) = NewRouter(db);

        var response = await router.SendAsync(new AiRequest { UserPrompt = "x" });

        response.Provider.Should().Be("only");
        legacy.Calls.Should().Be(0);
    }

    [Fact]
    public async Task UndecryptableKey_FailsWithoutCallingTheModel()
    {
        await using var db = NewDb();
        var broken = Model("broken", isDefault: true);
        broken.ProtectedApiKey = "garbage-not-encrypted-by-us";
        db.AiModelConfigs.Add(broken);
        await db.SaveChangesAsync();
        var (router, factory, _) = NewRouter(db);

        var response = await router.SendAsync(new AiRequest { UserPrompt = "x" });

        response.IsSuccess.Should().BeFalse();
        response.FailureKind.Should().Be(AiFailureKind.Permanent);
        response.ErrorMessage.Should().Contain("API key");
        factory.Last!.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(32000, 8000, 8000)]
    [InlineData(4000, 8000, 4000)]
    [InlineData(null, 8000, 8000)]
    public async Task MaxTokens_AreCappedByTheModelLimit(int? requested, int limit, int expected)
    {
        await using var db = NewDb();
        db.AiModelConfigs.Add(Model("capped", isDefault: true, maxTokens: limit));
        await db.SaveChangesAsync();
        var (router, factory, _) = NewRouter(db);

        await router.SendAsync(new AiRequest { UserPrompt = "x", MaxTokens = requested });

        factory.Last!.Request!.MaxTokens.Should().Be(expected);
    }

    [Fact]
    public async Task ParallelCalls_ShareOneSnapshotWithoutDbContextRaces()
    {
        await using var db = NewDb();
        db.AiModelConfigs.Add(Model("main", isDefault: true));
        await db.SaveChangesAsync();
        var (router, _, _) = NewRouter(db);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => router.SendAsync(new AiRequest { UserPrompt = "x" })));

        responses.Should().OnlyContain(r => r.IsSuccess && r.Provider == "main");
    }

    [Fact]
    public async Task PinnedModel_WinsOverStageAssignmentAndDefault()
    {
        await using var db = NewDb();
        var fallback = Model("fallback", isDefault: true);
        var assigned = Model("assigned");
        var pinned = Model("pinned");
        db.AiModelConfigs.AddRange(fallback, assigned, pinned);
        db.AiStageAssignments.Add(new AiStageAssignment { Id = Guid.NewGuid(), Stage = AiStage.CodeGeneration, AiModelConfigId = assigned.Id });
        await db.SaveChangesAsync();
        var (router, _, _) = NewRouter(db, new AiExecutionContext { PinnedModelId = pinned.Id });

        var response = await router.SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.CodeGeneration });

        response.Provider.Should().Be("pinned");
    }

    [Fact]
    public async Task PinnedModelThatNoLongerExists_FailsInsteadOfUsingAnotherModel()
    {
        await using var db = NewDb();
        db.AiModelConfigs.Add(Model("main", isDefault: true));
        await db.SaveChangesAsync();
        var (router, factory, legacy) = NewRouter(db, new AiExecutionContext { PinnedModelId = Guid.NewGuid() });

        var response = await router.SendAsync(new AiRequest { UserPrompt = "x" });

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Contain("pinned");
        factory.Created.Should().BeEmpty();
        legacy.Calls.Should().Be(0);
    }

    [Fact]
    public async Task PinnedModelThatIsDisabled_FailsToo()
    {
        await using var db = NewDb();
        var off = Model("off", enabled: false);
        db.AiModelConfigs.AddRange(Model("main", isDefault: true), off);
        await db.SaveChangesAsync();
        var (router, factory, _) = NewRouter(db, new AiExecutionContext { PinnedModelId = off.Id });

        var response = await router.SendAsync(new AiRequest { UserPrompt = "x" });

        response.IsSuccess.Should().BeFalse();
        factory.Created.Should().BeEmpty();
    }

    // ---- OpenAiCompatibleProvider wire format ----

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/chat/completions")]
    public async Task OpenAiCompatibleProvider_BuildsTheRightUrl(string baseUrl, string expectedUrl)
    {
        var handler = new CapturingHandler();
        var provider = NewWireProvider(handler, new OpenAiCompatibleSettings
        {
            ProviderName = "p", DisplayName = "p", BaseUrl = baseUrl, Model = "m", ApiKey = "k", Stream = false,
        });

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "hi" });

        response.IsSuccess.Should().BeTrue();
        handler.Uri!.ToString().Should().Be(expectedUrl);
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_OmitsReasoningEffort_WhenModelDoesNotSupportIt()
    {
        var handler = new CapturingHandler();
        var provider = NewWireProvider(handler, new OpenAiCompatibleSettings
        {
            ProviderName = "p", DisplayName = "p", BaseUrl = "https://x.example", Model = "m", ApiKey = "k",
            Stream = false, SupportsReasoningEffort = false,
        });

        await provider.SendAsync(new AiRequest { UserPrompt = "hi", ReasoningEffort = "low", MaxTokens = 100 });

        handler.Body.Should().NotContain("reasoning_effort");
        handler.Body.Should().Contain("\"max_tokens\":100");
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_UsesMaxCompletionTokens_WhenConfigured()
    {
        var handler = new CapturingHandler();
        var provider = NewWireProvider(handler, new OpenAiCompatibleSettings
        {
            ProviderName = "p", DisplayName = "p", BaseUrl = "https://x.example", Model = "m", ApiKey = "k",
            Stream = false, UseMaxCompletionTokens = true, SupportsReasoningEffort = true,
        });

        await provider.SendAsync(new AiRequest { UserPrompt = "hi", ReasoningEffort = "low", MaxTokens = 100 });

        handler.Body.Should().Contain("\"max_completion_tokens\":100");
        handler.Body.Should().NotContain("\"max_tokens\"");
        handler.Body.Should().Contain("\"reasoning_effort\":\"low\"");
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_LocalRuntimeWorksWithoutApiKey()
    {
        var handler = new CapturingHandler();
        var provider = NewWireProvider(handler, new OpenAiCompatibleSettings
        {
            ProviderName = "ollama", DisplayName = "ollama", BaseUrl = "http://localhost:11434", Model = "llama3",
            RequiresApiKey = false, Stream = false,
        });

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "hi" });

        response.IsSuccess.Should().BeTrue();
    }

    private static OpenAiCompatibleProvider NewWireProvider(CapturingHandler handler, OpenAiCompatibleSettings settings) =>
        new(new SingleClientFactory(handler), settings, NullLogger.Instance);

    // ---- fakes ----

    internal sealed class FakeProtector : IAiKeyProtector
    {
        public static string Protected(string plain) => "enc:" + plain;

        public string Protect(string plainText) => Protected(plainText);

        public string? Unprotect(string protectedText) =>
            protectedText.StartsWith("enc:", StringComparison.Ordinal) ? protectedText[4..] : null;
    }

    internal sealed class RecordingProvider : IAiProvider
    {
        public RecordingProvider(string name) => ProviderName = name;

        public string ProviderName { get; }

        public int Calls { get; private set; }

        public AiRequest? Request { get; private set; }

        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            return Task.FromResult(new AiResponse { Provider = ProviderName, Model = request.Model, Content = "ok", IsSuccess = true });
        }
    }

    internal sealed class FakeFactory : IAiProviderFactory
    {
        public List<RecordingProvider> Created { get; } = new();

        public RecordingProvider? Last => Created.LastOrDefault();

        public IAiProvider? Create(AiModelConfig config, string? apiKey, bool forConnectionTest = false)
        {
            var provider = new RecordingProvider(config.Name);
            Created.Add(provider);
            return provider;
        }

        public bool RequiresApiKey(AiModelConfig config) => true;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            const string json = """{"model":"m","choices":[{"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
