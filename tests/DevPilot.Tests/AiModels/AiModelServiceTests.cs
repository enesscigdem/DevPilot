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

public class AiModelServiceTests
{
    private static DevPilotDbContext NewDb() =>
        new(new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static AiModelService NewService(DevPilotDbContext db, AiModelRoutingTests.FakeFactory? factory = null) =>
        new(db, new AiModelRoutingTests.FakeProtector(), factory ?? new AiModelRoutingTests.FakeFactory());

    /// <summary>A provider that waits until it is cancelled, like a real one stuck on a slow endpoint.</summary>
    private sealed class HangingFactory : IAiProviderFactory
    {
        public bool RequiresApiKey(AiModelConfig config) => false;

        public IAiProvider? Create(AiModelConfig config, string? apiKey, bool forConnectionTest = false) => new HangingProvider();
    }

    private sealed class HangingProvider : IAiProvider
    {
        public string ProviderName => "hang";

        public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            return new AiResponse { IsSuccess = false, FailureKind = AiFailureKind.Cancelled, ErrorMessage = "cancelled" };
        }
    }

    private static SaveAiModelRequest Request(string name = "DeepSeek", string url = "https://api.deepseek.com", string? key = "sk-abcdef123456") =>
        new() { Name = name, BaseUrl = url, ModelName = "deepseek-chat", ApiKey = key };

    [Fact]
    public async Task Create_NeverReturnsTheKey_AndShowsOnlyAHint()
    {
        await using var db = NewDb();
        var created = await NewService(db).CreateAsync(Request(), CancellationToken.None);

        created.HasApiKey.Should().BeTrue();
        created.ApiKeyHint.Should().Be("...3456");
        System.Text.Json.JsonSerializer.Serialize(created).Should().NotContain("abcdef");

        var stored = await db.AiModelConfigs.SingleAsync();
        stored.ProtectedApiKey.Should().NotBe("sk-abcdef123456");
    }

    [Theory]
    [InlineData("https://yapayzekalab.org/v1/chat/completions", "https://yapayzekalab.org/v1")]
    [InlineData("https://yapayzekalab.org/chat/completions/", "https://yapayzekalab.org")]
    [InlineData("  https://api.deepseek.com/  ", "https://api.deepseek.com")]
    public async Task Create_CleansUpPastedEndpointPaths(string typed, string stored)
    {
        await using var db = NewDb();

        var created = await NewService(db).CreateAsync(Request(url: typed), CancellationToken.None);

        created.BaseUrl.Should().Be(stored);
    }

    [Fact]
    public async Task FirstModel_BecomesDefault_SecondDoesNot()
    {
        await using var db = NewDb();
        var service = NewService(db);

        var first = await service.CreateAsync(Request("A"), CancellationToken.None);
        var second = await service.CreateAsync(Request("B"), CancellationToken.None);

        first.IsDefault.Should().BeTrue();
        second.IsDefault.Should().BeFalse();
    }

    [Theory]
    [InlineData("", "https://x.example", "m")]
    [InlineData("n", "not-a-url", "m")]
    [InlineData("n", "ftp://x.example", "m")]
    [InlineData("n", "https://x.example", " ")]
    public async Task Create_RejectsInvalidInput(string name, string url, string modelName)
    {
        await using var db = NewDb();
        var request = Request(name, url);
        request.ModelName = modelName;

        var act = () => NewService(db).CreateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Create_RejectsUnknownAdapterTypes()
    {
        await using var db = NewDb();
        var request = Request();
        request.AdapterType = (AiAdapterType)99;

        var act = () => NewService(db).CreateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Unknown adapter*");
    }

    [Fact]
    public async Task Create_RejectsDuplicateNamesIgnoringCase()
    {
        await using var db = NewDb();
        var service = NewService(db);
        await service.CreateAsync(Request("DeepSeek"), CancellationToken.None);

        var act = () => service.CreateAsync(Request("deepseek"), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task Update_WithoutNewKey_KeepsTheStoredKey()
    {
        await using var db = NewDb();
        var service = NewService(db);
        var created = await service.CreateAsync(Request(), CancellationToken.None);
        var before = (await db.AiModelConfigs.SingleAsync()).ProtectedApiKey;

        var update = Request(key: null);
        update.MaxOutputTokens = 4096;
        var updated = await service.UpdateAsync(created.Id, update, CancellationToken.None);

        updated.MaxOutputTokens.Should().Be(4096);
        updated.HasApiKey.Should().BeTrue();
        (await db.AiModelConfigs.SingleAsync()).ProtectedApiKey.Should().Be(before);
    }

    [Fact]
    public async Task Update_ChangingTheHost_RequiresReEnteringTheKey()
    {
        await using var db = NewDb();
        var service = NewService(db);
        var created = await service.CreateAsync(Request(), CancellationToken.None);

        var act = () => service.UpdateAsync(created.Id, Request(url: "https://evil.example", key: null), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Enter the API key again*");
    }

    [Fact]
    public async Task Update_UnknownId_ThrowsNotFound()
    {
        await using var db = NewDb();

        var act = () => NewService(db).UpdateAsync(Guid.NewGuid(), Request(), CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Delete_RemovesTheModelAndItsStageAssignments()
    {
        await using var db = NewDb();
        var service = NewService(db);
        var model = await service.CreateAsync(Request(), CancellationToken.None);
        await service.SetStageAssignmentsAsync(
            new[] { new AiStageAssignmentDto { Stage = AiStage.Repair, AiModelConfigId = model.Id } },
            CancellationToken.None);

        await service.DeleteAsync(model.Id, CancellationToken.None);

        (await db.AiModelConfigs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task StageAssignments_ListEveryStage_AndAcceptAssignAndClear()
    {
        await using var db = NewDb();
        var service = NewService(db);
        var model = await service.CreateAsync(Request(), CancellationToken.None);

        var initial = await service.GetStageAssignmentsAsync(CancellationToken.None);
        initial.Select(a => a.Stage).Should().BeEquivalentTo(Enum.GetValues<AiStage>());
        initial.Should().OnlyContain(a => a.AiModelConfigId == null);

        var assigned = await service.SetStageAssignmentsAsync(
            new[] { new AiStageAssignmentDto { Stage = AiStage.Planning, AiModelConfigId = model.Id } },
            CancellationToken.None);
        assigned.Single(a => a.Stage == AiStage.Planning).AiModelConfigId.Should().Be(model.Id);

        var cleared = await service.SetStageAssignmentsAsync(
            new[] { new AiStageAssignmentDto { Stage = AiStage.Planning, AiModelConfigId = null } },
            CancellationToken.None);
        cleared.Should().OnlyContain(a => a.AiModelConfigId == null);
    }

    [Fact]
    public async Task StageAssignments_RejectUnknownOrDisabledModels()
    {
        await using var db = NewDb();
        var service = NewService(db);
        var disabledRequest = Request("Off");
        disabledRequest.IsEnabled = false;
        var disabled = await service.CreateAsync(disabledRequest, CancellationToken.None);

        var unknown = () => service.SetStageAssignmentsAsync(
            new[] { new AiStageAssignmentDto { Stage = AiStage.Repair, AiModelConfigId = Guid.NewGuid() } },
            CancellationToken.None);
        var off = () => service.SetStageAssignmentsAsync(
            new[] { new AiStageAssignmentDto { Stage = AiStage.Repair, AiModelConfigId = disabled.Id } },
            CancellationToken.None);

        await unknown.Should().ThrowAsync<ArgumentException>();
        await off.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Test_ThatNeverAnswers_EndsWithAClearMessage_AndIsRecorded()
    {
        await using var db = NewDb();
        var service = new AiModelService(db, new AiModelRoutingTests.FakeProtector(), new HangingFactory(), TimeSpan.FromMilliseconds(150));
        var model = await NewService(db).CreateAsync(Request(), CancellationToken.None);

        var result = await service.TestAsync(model.Id, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("No answer within");
        var stored = await db.AiModelConfigs.SingleAsync();
        stored.LastTestSucceeded.Should().BeFalse();
        stored.LastTestMessage.Should().Contain("No answer");
        result.Outcome.Should().Be("Timeout");
        result.StatusCode.Should().BeNull();
        stored.LastTestOutcome.Should().Be("Timeout");
    }

    [Theory]
    [InlineData(401, AiFailureKind.Permanent, "HttpError", 401)]
    [InlineData(503, AiFailureKind.TransientServiceUnavailable, "HttpError", 503)]
    [InlineData(null, AiFailureKind.TimeoutOrConnection, "NetworkError", null)]
    public async Task Test_ProviderErrors_AreNotReportedAsOurTimeout(int? status, AiFailureKind kind, string outcome, int? expectedStatus)
    {
        await using var db = NewDb();
        var service = new AiModelService(db, new AiModelRoutingTests.FakeProtector(), new FailingFactory(status, kind));
        var model = await NewService(db).CreateAsync(Request(), CancellationToken.None);

        var result = await service.TestAsync(model.Id, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(outcome);
        result.StatusCode.Should().Be(expectedStatus);
        result.Message.Should().NotContain("No answer within");
        var stored = await db.AiModelConfigs.SingleAsync();
        stored.LastTestOutcome.Should().Be(outcome);
        stored.LastTestStatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task Test_UsesAOneHundredTwentySecondLimit_ByDefault()
    {
        await using var db = NewDb();
        var service = NewService(db);
        var model = await service.CreateAsync(Request(), CancellationToken.None);

        var result = await service.TestAsync(model.Id, CancellationToken.None);

        result.Outcome.Should().Be("Ok");
        result.TimeLimitSeconds.Should().Be(120);
    }

    private sealed class FailingFactory : IAiProviderFactory
    {
        private readonly int? _status;
        private readonly AiFailureKind _kind;

        public FailingFactory(int? status, AiFailureKind kind)
        {
            _status = status;
            _kind = kind;
        }

        public bool RequiresApiKey(AiModelConfig config) => false;

        public IAiProvider? Create(AiModelConfig config, string? apiKey, bool forConnectionTest = false) => new FailingProvider(_status, _kind);
    }

    private sealed class FailingProvider : IAiProvider
    {
        private readonly int? _status;
        private readonly AiFailureKind _kind;

        public FailingProvider(int? status, AiFailureKind kind)
        {
            _status = status;
            _kind = kind;
        }

        public string ProviderName => "fail";

        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiResponse { IsSuccess = false, StatusCode = _status, FailureKind = _kind, ErrorMessage = "provider said no" });
    }

    [Fact]
    public async Task Test_CancelledByTheCaller_PropagatesTheCancellation_InsteadOfReportingATimeout()
    {
        await using var db = NewDb();
        var service = new AiModelService(db, new AiModelRoutingTests.FakeProtector(), new HangingFactory(), TimeSpan.FromSeconds(30));
        var model = await NewService(db).CreateAsync(Request(), CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => service.TestAsync(model.Id, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Test_RecordsTheOutcomeOnTheModel()
    {
        await using var db = NewDb();
        var service = NewService(db);
        var model = await service.CreateAsync(Request(), CancellationToken.None);

        var result = await service.TestAsync(model.Id, CancellationToken.None);

        result.Success.Should().BeTrue();
        var stored = await db.AiModelConfigs.SingleAsync();
        stored.LastTestSucceeded.Should().BeTrue();
        stored.LastTestedAt.Should().NotBeNull();
    }
}
