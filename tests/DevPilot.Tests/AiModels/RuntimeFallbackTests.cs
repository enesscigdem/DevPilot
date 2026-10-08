using DevPilot.Application.AiProviders;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.AiProviders;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DevPilot.Tests.AiModels;

/// <summary>A model that is rate limited or unreachable must not stall a run for minutes while another model is ready.</summary>
public class RuntimeFallbackTests
{
    private static DevPilotDbContext NewDb() =>
        new(new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static AiModelConfig Model(string name, bool isDefault = false, string? key = "sk-test", int? maxTokens = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            AdapterType = AiAdapterType.OpenAiCompatible,
            BaseUrl = "https://api.example.com",
            ModelName = $"{name}-model",
            ProtectedApiKey = key is null ? null : AiModelRoutingTests.FakeProtector.Protected(key),
            IsDefault = isDefault,
            IsEnabled = true,
            MaxOutputTokens = maxTokens,
        };

    private static async Task<(RoutingAiProvider Router, ScriptedFactory Factory, List<AiAttemptEvent> Events)> ArrangeAsync(
        DevPilotDbContext db,
        AiModelConfig assigned,
        params AiModelConfig[] others)
    {
        db.AiModelConfigs.Add(assigned);
        db.AiModelConfigs.AddRange(others);
        db.AiStageAssignments.Add(new AiStageAssignment { Id = Guid.NewGuid(), Stage = AiStage.Repair, AiModelConfigId = assigned.Id });
        await db.SaveChangesAsync();

        var events = new List<AiAttemptEvent>();
        var factory = new ScriptedFactory();
        var context = new AiExecutionContext { AttemptObserver = e => { events.Add(e); return Task.CompletedTask; } };
        var router = new RoutingAiProvider(db, new AiModelRoutingTests.FakeProtector(), factory, new AiModelRoutingTests.RecordingProvider("legacy"), context);
        return (router, factory, events);
    }

    private static AiRequest Repair(int? maxTokens = null) =>
        new() { UserPrompt = "fix it", Stage = AiStage.Repair, MaxTokens = maxTokens };

    [Theory]
    [InlineData(AiFailureKind.RateLimited, "rate limited")]
    [InlineData(AiFailureKind.TimeoutOrConnection, "not answering")]
    [InlineData(AiFailureKind.TransientServiceUnavailable, "unavailable")]
    public async Task AFailureThatAnotherModelCanAnswer_IsHandedToTheDefaultModel(AiFailureKind kind, string described)
    {
        await using var db = NewDb();
        var claude = Model("claude");
        var gemini = Model("gemini", isDefault: true);
        var (router, factory, events) = await ArrangeAsync(db, claude, gemini);
        factory.Script["claude"] = _ => Fail(kind);

        var response = await router.SendAsync(Repair());

        response.IsSuccess.Should().BeTrue();
        response.Provider.Should().Be("gemini");
        response.ModelConfigId.Should().Be(gemini.Id);
        response.RoutingSource.Should().Be("RuntimeFallback");
        response.FallbackReason.Should().Contain("claude").And.Contain(described).And.Contain("gemini answered instead");
        events.Should().Contain(e => e.Kind == "ModelFallback" && e.Model!.Contains("gemini"));
        factory.CallsTo("claude").Should().Be(1);
        factory.CallsTo("gemini").Should().Be(1);
    }

    [Fact]
    public async Task TheAssignedModelIsOnlyRetriedTwiceWhenAnotherModelIsReady()
    {
        await using var db = NewDb();
        var (router, factory, _) = await ArrangeAsync(db, Model("claude"), Model("gemini", isDefault: true));
        factory.Script["claude"] = _ => Fail(AiFailureKind.RateLimited);

        await router.SendAsync(Repair());

        factory.RequestsTo("claude").Single().MaxAttempts.Should().Be(RoutingAiProvider.PrimaryAttemptsWhenAnAlternateExists);
        factory.RequestsTo("gemini").Single().MaxAttempts.Should().BeNull("the alternate keeps the provider's full retries");
    }

    [Fact]
    public async Task TheAlternateIsAskedWithItsOwnModelNameAndTokenCap()
    {
        await using var db = NewDb();
        var (router, factory, _) = await ArrangeAsync(db, Model("claude"), Model("gemini", isDefault: true, maxTokens: 1000));
        factory.Script["claude"] = _ => Fail(AiFailureKind.RateLimited);

        await router.SendAsync(Repair(maxTokens: 5000));

        var request = factory.RequestsTo("gemini").Single();
        request.Model.Should().Be("gemini-model");
        request.MaxTokens.Should().Be(1000);
        request.SystemPrompt.Should().BeNull();
        request.UserPrompt.Should().Be("fix it");
    }

    [Fact]
    public async Task ASuccessfulCall_NeverTouchesTheAlternate()
    {
        await using var db = NewDb();
        var (router, factory, _) = await ArrangeAsync(db, Model("claude"), Model("gemini", isDefault: true));

        var response = await router.SendAsync(Repair());

        response.Provider.Should().Be("claude");
        response.RoutingSource.Should().Be("StageAssignment");
        factory.CallsTo("gemini").Should().Be(0);
    }

    [Theory]
    [InlineData(AiFailureKind.Permanent)]
    [InlineData(AiFailureKind.TokenLimitExceeded)]
    [InlineData(AiFailureKind.Cancelled)]
    public async Task AFailureAnotherModelWouldNotFix_IsNotHandedOver(AiFailureKind kind)
    {
        await using var db = NewDb();
        var (router, factory, _) = await ArrangeAsync(db, Model("claude"), Model("gemini", isDefault: true));
        factory.Script["claude"] = _ => Fail(kind);

        var response = await router.SendAsync(Repair());

        response.IsSuccess.Should().BeFalse();
        response.FailureKind.Should().Be(kind);
        factory.CallsTo("gemini").Should().Be(0);
    }

    [Fact]
    public async Task WithNoOtherModel_TheAssignedOneKeepsItsFullRetries()
    {
        await using var db = NewDb();
        var (router, factory, _) = await ArrangeAsync(db, Model("claude"));
        factory.Script["claude"] = _ => Fail(AiFailureKind.RateLimited);

        var response = await router.SendAsync(Repair());

        response.IsSuccess.Should().BeFalse();
        response.FailureKind.Should().Be(AiFailureKind.RateLimited);
        factory.RequestsTo("claude").Single().MaxAttempts.Should().BeNull();
    }

    [Fact]
    public async Task AModelWithoutAUsableKey_IsNotUsedAsTheAlternate()
    {
        await using var db = NewDb();
        var (router, factory, _) = await ArrangeAsync(db, Model("claude"), Model("gemini", isDefault: true, key: null));
        factory.Script["claude"] = _ => Fail(AiFailureKind.RateLimited);

        var response = await router.SendAsync(Repair());

        response.IsSuccess.Should().BeFalse();
        factory.CallsTo("gemini").Should().Be(0);
        factory.RequestsTo("claude").Single().MaxAttempts.Should().BeNull("no alternate exists, so retrying is all there is");
    }

    [Fact]
    public async Task TheDefaultModelIsPreferred_ThenModelsByName()
    {
        await using var db = NewDb();
        var (router, factory, _) = await ArrangeAsync(db, Model("claude"), Model("zeta"), Model("alpha"), Model("middle", isDefault: true));
        factory.Script["claude"] = _ => Fail(AiFailureKind.RateLimited);
        factory.Script["middle"] = _ => Fail(AiFailureKind.RateLimited);

        var response = await router.SendAsync(Repair());

        response.IsSuccess.Should().BeFalse();
        factory.CallsTo("middle").Should().Be(1);
        factory.CallsTo("alpha").Should().Be(0, "only one alternate is tried per call");
    }

    [Fact]
    public async Task WithNoDefaultModel_TheAlphabeticallyFirstOtherModelTakesOver()
    {
        await using var db = NewDb();
        var (router, factory, _) = await ArrangeAsync(db, Model("claude"), Model("zeta"), Model("alpha"));
        factory.Script["claude"] = _ => Fail(AiFailureKind.RateLimited);

        var response = await router.SendAsync(Repair());

        response.Provider.Should().Be("alpha");
    }

    [Fact]
    public async Task WithNoDefaultModel_TheModelAssignedToCodeGenerationTakesOverBeforeAlphabeticalOrder()
    {
        await using var db = NewDb();
        var claude = Model("claude");
        var workhorse = Model("zeta-workhorse");
        var opus = Model("alpha-expensive");
        db.AiModelConfigs.AddRange(claude, workhorse, opus);
        db.AiStageAssignments.Add(new AiStageAssignment { Id = Guid.NewGuid(), Stage = AiStage.Repair, AiModelConfigId = claude.Id });
        db.AiStageAssignments.Add(new AiStageAssignment { Id = Guid.NewGuid(), Stage = AiStage.CodeGeneration, AiModelConfigId = workhorse.Id });
        await db.SaveChangesAsync();
        var factory = new ScriptedFactory();
        factory.Script["claude"] = _ => Fail(AiFailureKind.RateLimited);
        var router = new RoutingAiProvider(
            db, new AiModelRoutingTests.FakeProtector(), factory, new AiModelRoutingTests.RecordingProvider("legacy"), new AiExecutionContext());

        var response = await router.SendAsync(Repair());

        response.Provider.Should().Be("zeta-workhorse");
        factory.CallsTo("alpha-expensive").Should().Be(0);
    }

    [Fact]
    public async Task TheDefaultModelStillBeatsTheCodeGenerationModel()
    {
        await using var db = NewDb();
        var claude = Model("claude");
        var workhorse = Model("workhorse");
        var preferred = Model("preferred", isDefault: true);
        db.AiModelConfigs.AddRange(claude, workhorse, preferred);
        db.AiStageAssignments.Add(new AiStageAssignment { Id = Guid.NewGuid(), Stage = AiStage.Repair, AiModelConfigId = claude.Id });
        db.AiStageAssignments.Add(new AiStageAssignment { Id = Guid.NewGuid(), Stage = AiStage.CodeGeneration, AiModelConfigId = workhorse.Id });
        await db.SaveChangesAsync();
        var factory = new ScriptedFactory();
        factory.Script["claude"] = _ => Fail(AiFailureKind.RateLimited);
        var router = new RoutingAiProvider(
            db, new AiModelRoutingTests.FakeProtector(), factory, new AiModelRoutingTests.RecordingProvider("legacy"), new AiExecutionContext());

        var response = await router.SendAsync(Repair());

        response.Provider.Should().Be("preferred");
    }

    [Fact]
    public async Task WhenBothModelsFail_TheAssignedModelsFailureIsReported_WithTheFallbackNoted()
    {
        await using var db = NewDb();
        var (router, factory, _) = await ArrangeAsync(db, Model("claude"), Model("gemini", isDefault: true));
        factory.Script["claude"] = _ => Fail(AiFailureKind.RateLimited);
        factory.Script["gemini"] = _ => Fail(AiFailureKind.TimeoutOrConnection);

        var response = await router.SendAsync(Repair());

        response.IsSuccess.Should().BeFalse();
        response.FailureKind.Should().Be(AiFailureKind.RateLimited);
        response.ModelConfigName.Should().Be("claude");
        response.FallbackReason.Should().Contain("gemini also failed");
    }

    [Fact]
    public async Task APinnedModel_IsNeverReplaced_AndKeepsItsRetries()
    {
        await using var db = NewDb();
        var claude = Model("claude");
        var gemini = Model("gemini", isDefault: true);
        db.AiModelConfigs.AddRange(claude, gemini);
        await db.SaveChangesAsync();
        var factory = new ScriptedFactory();
        factory.Script["claude"] = _ => Fail(AiFailureKind.RateLimited);
        var router = new RoutingAiProvider(
            db, new AiModelRoutingTests.FakeProtector(), factory, new AiModelRoutingTests.RecordingProvider("legacy"),
            new AiExecutionContext { PinnedModelId = claude.Id });

        var response = await router.SendAsync(Repair());

        response.IsSuccess.Should().BeFalse();
        response.RoutingSource.Should().Be("Pinned");
        factory.CallsTo("gemini").Should().Be(0);
        factory.RequestsTo("claude").Single().MaxAttempts.Should().BeNull();
    }

    private static AiResponse Fail(AiFailureKind kind) => new()
    {
        IsSuccess = false,
        FailureKind = kind,
        ErrorMessage = $"scripted {kind}",
    };

    private sealed class ScriptedProvider : IAiProvider
    {
        private readonly Func<AiRequest, AiResponse>? _script;

        public ScriptedProvider(string name, Func<AiRequest, AiResponse>? script)
        {
            ProviderName = name;
            _script = script;
        }

        public string ProviderName { get; }

        public List<AiRequest> Requests { get; } = new();

        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var response = _script?.Invoke(request)
                           ?? new AiResponse { IsSuccess = true, Content = "ok", Model = request.Model };
            response.Provider = ProviderName;
            return Task.FromResult(response);
        }
    }

    private sealed class ScriptedFactory : IAiProviderFactory
    {
        public Dictionary<string, Func<AiRequest, AiResponse>> Script { get; } = new();

        public List<ScriptedProvider> Created { get; } = new();

        public IAiProvider? Create(AiModelConfig config, string? apiKey, bool forConnectionTest = false)
        {
            Script.TryGetValue(config.Name, out var script);
            var provider = new ScriptedProvider(config.Name, script);
            Created.Add(provider);
            return provider;
        }

        public bool RequiresApiKey(AiModelConfig config) => true;

        public int CallsTo(string name) => Created.Where(p => p.ProviderName == name).Sum(p => p.Requests.Count);

        public List<AiRequest> RequestsTo(string name) => Created.Where(p => p.ProviderName == name).SelectMany(p => p.Requests).ToList();
    }
}
