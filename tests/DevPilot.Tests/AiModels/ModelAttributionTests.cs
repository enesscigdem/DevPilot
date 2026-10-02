using DevPilot.Application.AiProviders;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.AiProviders;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DevPilot.Tests.AiModels;

/// <summary>
/// Which model really answers a call, why it was chosen, and that the execution records it, instead of whatever
/// name the planning step happened to use.
/// </summary>
public class ModelAttributionTests
{
    private static DevPilotDbContext NewDb() =>
        new(new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static AiModelConfig Model(string name, bool isDefault = false, bool enabled = true) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            AdapterType = AiAdapterType.OpenAiCompatible,
            BaseUrl = "https://api.example.com",
            ModelName = $"{name}-model",
            ProtectedApiKey = AiModelRoutingTests.FakeProtector.Protected("sk-test-1234"),
            IsDefault = isDefault,
            IsEnabled = enabled,
        };

    private static RoutingAiProvider NewRouter(DevPilotDbContext db, IAiExecutionContext? context = null) =>
        new(db, new AiModelRoutingTests.FakeProtector(), new AiModelRoutingTests.FakeFactory(), new AiModelRoutingTests.RecordingProvider("legacy"), context);

    private static async Task Assign(DevPilotDbContext db, AiStage stage, AiModelConfig model)
    {
        db.AiStageAssignments.Add(new AiStageAssignment { Id = Guid.NewGuid(), Stage = stage, AiModelConfigId = model.Id });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CodeGenerationAssignedToGemini_UsesGemini_WhilePlanningAndRepairUseSol()
    {
        // The reported setup: coding on Gemini 3.7 Flash (high), the other steps on GPT-6.1 Sol.
        await using var db = NewDb();
        var sol = Model("GPT-6.1 Sol");
        var gemini = Model("Gemini 3.7 Flash (high)");
        db.AiModelConfigs.AddRange(sol, gemini);
        await db.SaveChangesAsync();
        await Assign(db, AiStage.Planning, sol);
        await Assign(db, AiStage.Repair, sol);
        await Assign(db, AiStage.CodeGeneration, gemini);
        var router = NewRouter(db);

        var coding = await router.SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.CodeGeneration, Model = "gpt-6.1-sol" });
        var repair = await router.SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.Repair });

        coding.ModelConfigName.Should().Be("Gemini 3.7 Flash (high)");
        coding.Model.Should().Be("Gemini 3.7 Flash (high)-model", "the caller's model name never overrides the assignment");
        coding.RoutingSource.Should().Be("StageAssignment");
        coding.FallbackReason.Should().BeNull();
        repair.ModelConfigName.Should().Be("GPT-6.1 Sol");
    }

    [Fact]
    public async Task AnUnassignedStage_RecordsThatTheDefaultWasUsedAndWhy()
    {
        await using var db = NewDb();
        db.AiModelConfigs.Add(Model("main", isDefault: true));
        await db.SaveChangesAsync();

        var response = await NewRouter(db).SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.Brain });

        response.RoutingSource.Should().Be("Default");
        response.FallbackReason.Should().Contain("No model is assigned to Brain");
    }

    [Fact]
    public async Task ADisabledAssignedModel_IsReportedAsTheReasonForTheFallback()
    {
        await using var db = NewDb();
        var main = Model("main", isDefault: true);
        var off = Model("off", enabled: false);
        db.AiModelConfigs.AddRange(main, off);
        await db.SaveChangesAsync();
        await Assign(db, AiStage.CodeGeneration, off);

        var response = await NewRouter(db).SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.CodeGeneration });

        response.ModelConfigName.Should().Be("main");
        response.FallbackReason.Should().Contain("disabled or deleted");
    }

    [Fact]
    public async Task WithNoModelsConfigured_TheLegacyProviderIsNamedAsTheFallback()
    {
        await using var db = NewDb();

        var response = await NewRouter(db).SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.Planning });

        response.RoutingSource.Should().Be("Legacy");
        response.FallbackReason.Should().Contain("legacy").And.Contain("appsettings provider");
    }

    [Fact]
    public async Task APinnedModel_IsReportedAsPinned()
    {
        await using var db = NewDb();
        var pinned = Model("pinned");
        db.AiModelConfigs.AddRange(pinned, Model("other", isDefault: true));
        await db.SaveChangesAsync();

        var response = await NewRouter(db, new AiExecutionContext { PinnedModelId = pinned.Id })
            .SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.CodeGeneration });

        response.RoutingSource.Should().Be("Pinned");
        response.ModelConfigName.Should().Be("pinned");
    }

    [Fact]
    public async Task TheChoiceIsAnnouncedOncePerStageAndModel_WithTheFallbackReason()
    {
        await using var db = NewDb();
        db.AiModelConfigs.Add(Model("main", isDefault: true));
        await db.SaveChangesAsync();
        var events = new List<AiAttemptEvent>();
        var context = new AiExecutionContext { AttemptObserver = e => { events.Add(e); return Task.CompletedTask; } };
        var router = NewRouter(db, context);

        await router.SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.CodeGeneration });
        await router.SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.CodeGeneration });
        await router.SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.Repair });

        events.Where(e => e.Kind == "ModelSelected").Select(e => e.Detail).Should().Equal(
            "CodeGeneration via default model", "Repair via default model");
        events.Where(e => e.Kind == "ModelFallback").Should().HaveCount(2);
        events.First(e => e.Kind == "ModelSelected").Model.Should().Be("main (main-model)");
    }

    [Fact]
    public async Task RecordingTheModelOnTheExecution_NeverFailsTheCall()
    {
        // The provider-specific write (a bulk update) is not supported by the in-memory test database; the call must
        // still succeed because recording the model is telemetry. The write itself is a plain ExecuteUpdate on Postgres.
        await using var db = NewDb();
        var gemini = Model("Gemini");
        db.AiModelConfigs.Add(gemini);
        await db.SaveChangesAsync();
        await Assign(db, AiStage.CodeGeneration, gemini);
        var router = NewRouter(db, new AiExecutionContext { ExecutionId = Guid.NewGuid() });

        var response = await router.SendAsync(new AiRequest { UserPrompt = "x", Stage = AiStage.CodeGeneration, Model = "gpt-6.1-sol" });

        response.IsSuccess.Should().BeTrue();
        response.ModelConfigName.Should().Be("Gemini");
    }

    [Fact]
    public async Task BindingRecordsAttemptsAndModelChoicesOnTheExecutionFeed_WithoutAffectingTheVerdict()
    {
        var recorder = new Recorder();
        var context = new AiExecutionContext();
        var executionId = Guid.NewGuid();

        AiExecutionBinding.Bind(context, executionId, null, recorder);
        await context.AttemptObserver!(new AiAttemptEvent(1, 4, "IdleTimeout", 91_000, true, 2000, Model: "gemini"));

        context.ExecutionId.Should().Be(executionId);
        var recorded = recorder.Items.Should().ContainSingle().Subject;
        recorded.Stage.Should().Be(ExecutionStage.DeveloperAgent);
        recorded.Status.Should().Be(ExecutionActivityStatus.Started, "a Started attempt event never flips the Implement or verdict state");
        recorded.Meta!.EventKind.Should().Be("ProviderAttempt");
        recorded.Meta.ProviderCallKind.Should().Be("Attempt");
        recorded.Meta.AttemptOutcome.Should().Be("IdleTimeout");
        recorded.Meta.WillRetry.Should().BeTrue();
        recorded.Message.Should().Contain("stopped sending data");
    }

    private sealed class Recorder : IExecutionActivityRecorder
    {
        public List<(ExecutionStage Stage, ExecutionActivityStatus Status, string Message, ExecutionActivityMetadata? Meta)> Items { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Items.Add((stage, status, message, metadata));
            return Task.CompletedTask;
        }
    }
}
