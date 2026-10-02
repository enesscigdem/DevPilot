using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Services;
using DevPilot.Application.RepositoryWorkspaces.Ports;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetWorkspaceInsights;
using DevPilot.Application.RepositoryWorkspaces.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.RepositoryWorkspaces;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.RepositoryWorkspaces;

public sealed class WorkspaceInsightsTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    // ── Builder ────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ComputesRatesFromMeasuredRuns_AndExcludesCancelled()
    {
        var inputs = new[]
        {
            Input("Verified", minutesAgo: 50, durationSec: 60),
            Input("NoNewRegressions", minutesAgo: 40, durationSec: 120),
            Input("PartiallyVerified", minutesAgo: 30, durationSec: 180, compile: 1),
            Input("NeedsReview", minutesAgo: 20, durationSec: 300, test: 2, failureReason: "The tests failure persisted after the focused repair, so repair stopped with no diagnostic progress."),
            Input("Blocked", minutesAgo: 10, durationSec: 5, status: TaskExecutionStatus.Cancelled),
        };

        var insights = WorkspaceInsightsBuilder.Build(inputs, Now);
        var t = insights.Totals;

        t.Executions.Should().Be(5);
        t.Cancelled.Should().Be(1);
        t.Measured.Should().Be(4);
        t.DeliveryReady.Should().Be(3);
        t.DeliveryReadyRate.Should().Be(0.75);
        t.VerifiedRate.Should().Be(0.5);
        t.FirstPass.Should().Be(2);
        t.FirstPassRate.Should().Be(0.5);
        t.Repaired.Should().Be(2);
        t.RepairedRecovered.Should().Be(1);
        t.RepairRecoveryRate.Should().Be(0.5);
        t.AvgDurationSeconds.Should().Be(165);
        t.MedianDurationSeconds.Should().Be(180);

        insights.Outcomes.Should().NotContain(o => o.Outcome == "Blocked");
        insights.TopFailureReasons.Should().ContainSingle(r => r.Reason.Contains("no diagnostic progress") && r.Count == 1);
    }

    [Fact]
    public void Build_CountsDifferentiatorSignals()
    {
        var inputs = new[]
        {
            Input("NoNewRegressions", 5),
            Input("PartiallyVerified", 4, baselineUnverified: true),
            Input("Verified", 3, flake: true),
            Input("NeedsReview", 2, weakening: true),
            Input("Verified", 1, staleBase: true),
        };

        var signals = WorkspaceInsightsBuilder.Build(inputs, Now).Signals.ToDictionary(s => s.Key, s => s.Count);

        signals["PreExistingSeparated"].Should().Be(1);
        signals["BaselineUnverified"].Should().Be(1);
        signals["FlakeAbsorbed"].Should().Be(1);
        signals["TestWeakeningCaught"].Should().Be(1);
        signals["StaleBase"].Should().Be(1);
    }

    [Fact]
    public void Build_TracksRetriedTasks_AttemptsAndImprovement()
    {
        var retriedTask = Guid.NewGuid();
        var first = Input("NeedsReview", 30, taskId: retriedTask, title: "Add export");
        var second = Input("Verified", 10, taskId: retriedTask, title: "Add export");
        var single = Input("Verified", 20);

        var insights = WorkspaceInsightsBuilder.Build(new[] { second, single, first }, Now);

        var retried = insights.RetriedTasks.Should().ContainSingle().Subject;
        retried.Attempts.Should().Be(2);
        retried.FirstExecutionId.Should().Be(first.ExecutionId);
        retried.LastExecutionId.Should().Be(second.ExecutionId);
        retried.FirstOutcome.Should().Be("NeedsReview");
        retried.LastOutcome.Should().Be("Verified");
        retried.Improved.Should().BeTrue();

        insights.Recent.Select(r => r.ExecutionId).Should().Equal(second.ExecutionId, single.ExecutionId, first.ExecutionId);
        insights.Recent.Single(r => r.ExecutionId == second.ExecutionId).AttemptNumber.Should().Be(2);
        insights.Recent.Single(r => r.ExecutionId == first.ExecutionId).AttemptNumber.Should().Be(1);
    }

    [Fact]
    public void Build_UsageTotals_UseOnlyRunsWithProviderCalls_AndSumKnownCostsOnly()
    {
        var withCost = new[]
        {
            Input("Verified", 3, calls: 4, tokens: 4000, cost: 0.02m),
            Input("Verified", 2, calls: 2, tokens: 2000, cost: 0.01m),
            Input("Verified", 1),
        };

        var t = WorkspaceInsightsBuilder.Build(withCost, Now).Totals;
        t.TotalTokens.Should().Be(6000);
        t.AvgTokensPerExecution.Should().Be(3000);
        t.AvgProviderCalls.Should().Be(3.0);
        t.TotalCostUsd.Should().Be(0.03m);

        var noCost = WorkspaceInsightsBuilder.Build(new[] { Input("Verified", 1, calls: 1, tokens: 100) }, Now).Totals;
        noCost.TotalCostUsd.Should().BeNull("no price table, no invented cost");
    }

    [Fact]
    public void Build_NoExecutions_ReturnsNullRatesNotZeroPercent()
    {
        var insights = WorkspaceInsightsBuilder.Build(Array.Empty<InsightExecutionInput>(), Now);

        insights.Totals.Executions.Should().Be(0);
        insights.Totals.DeliveryReadyRate.Should().BeNull();
        insights.Totals.MedianDurationSeconds.Should().BeNull();
        insights.Recent.Should().BeEmpty();
    }

    // ── Reader + handler ───────────────────────────────────────────────────────

    [Fact]
    public async Task Reader_UsesPersistedSnapshot_AndFallsBackToRecordedActivities()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase("Insights_" + Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new DevPilotDbContext(options);

        var workspace = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(), Owner = "o", Repository = "r", Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed, CreatedAt = Now, UpdatedAt = Now,
        };
        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(), RepositoryWorkspaceId = workspace.Id, Title = "Task", Status = DevelopmentTaskStatus.Completed,
            CreatedAt = Now, UpdatedAt = Now,
        };

        var persistedSnapshot = Snapshot("Verified");
        var withSnapshot = new TaskExecution
        {
            Id = Guid.NewGuid(), DevelopmentTaskId = task.Id, Status = TaskExecutionStatus.Completed,
            CreatedAt = Now.AddMinutes(-10), StartedAt = Now.AddMinutes(-10), CompletedAt = Now.AddMinutes(-9),
            VerificationOutcome = "Verified",
            VerificationSnapshotJson = ExecutionVerdictBuilder.Serialize(persistedSnapshot),
        };
        var legacy = new TaskExecution
        {
            Id = Guid.NewGuid(), DevelopmentTaskId = task.Id, Status = TaskExecutionStatus.Failed,
            CreatedAt = Now.AddMinutes(-5), StartedAt = Now.AddMinutes(-5), CompletedAt = Now.AddMinutes(-4),
            ErrorMessage = "Developer Agent failed: boom",
        };
        var running = new TaskExecution
        {
            Id = Guid.NewGuid(), DevelopmentTaskId = task.Id, Status = TaskExecutionStatus.Running, CreatedAt = Now,
        };

        db.RepositoryWorkspaces.Add(workspace);
        db.DevelopmentTasks.Add(task);
        db.TaskExecutions.AddRange(withSnapshot, legacy, running);
        db.ExecutionActivities.Add(new ExecutionActivity
        {
            Id = Guid.NewGuid(), ExecutionId = legacy.Id, Stage = ExecutionStage.DeveloperAgent,
            Status = ExecutionActivityStatus.Failed, Message = "Developer Agent failed: boom", CreatedAt = Now.AddMinutes(-5),
        });
        await db.SaveChangesAsync();

        var reader = new EfWorkspaceInsightsReader(db);
        var inputs = await reader.ReadAsync(workspace.Id, 50);

        inputs.Should().NotBeNull();
        inputs!.Should().HaveCount(2, "running executions are not finished and are excluded");
        inputs.Single(i => i.ExecutionId == withSnapshot.Id).Snapshot.Verdict.Outcome.Should().Be("Verified");
        inputs.Single(i => i.ExecutionId == legacy.Id).Snapshot.Verdict.Outcome.Should().Be("Failed");

        (await reader.ReadAsync(Guid.NewGuid(), 50)).Should().BeNull();

        var handler = new GetWorkspaceInsightsQueryHandler(reader, NullLogger<GetWorkspaceInsightsQueryHandler>.Instance);
        var ok = await handler.HandleAsync(new GetWorkspaceInsightsQuery(workspace.Id));
        ok.Success.Should().BeTrue();
        ok.Insights!.Totals.Measured.Should().Be(2);

        var missing = await handler.HandleAsync(new GetWorkspaceInsightsQuery(Guid.NewGuid()));
        missing.NotFound.Should().BeTrue();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ExecutionVerificationSnapshot Snapshot(
        string outcome,
        int compile = 0,
        int test = 0,
        int applicability = 0,
        bool baselineUnverified = false,
        bool flake = false,
        bool weakening = false,
        bool staleBase = false,
        string? failureReason = null,
        int calls = 0,
        long tokens = 0,
        decimal? cost = null)
    {
        var findings = new List<VerdictFindingDto>();
        if (failureReason != null)
        {
            findings.Add(new VerdictFindingDto("FailureReason", "danger", failureReason));
        }

        return new ExecutionVerificationSnapshot(
            ExecutionVerdictBuilder.SnapshotSchemaVersion,
            Now,
            new ExecutionVerdictDto(
                outcome, "neutral", $"{outcome} headline", null, findings,
                baselineUnverified, flake, weakening, staleBase,
                compile, test, 0, applicability, Array.Empty<string>()),
            new ExecutionUsageDto(calls, 0, 0, tokens / 2, tokens - tokens / 2, tokens, 0, cost, Array.Empty<ExecutionStageTimingDto>()));
    }

    private static InsightExecutionInput Input(
        string outcome,
        int minutesAgo,
        int durationSec = 60,
        TaskExecutionStatus status = TaskExecutionStatus.Completed,
        Guid? taskId = null,
        string title = "Task",
        int compile = 0,
        int test = 0,
        bool baselineUnverified = false,
        bool flake = false,
        bool weakening = false,
        bool staleBase = false,
        string? failureReason = null,
        int calls = 0,
        long tokens = 0,
        decimal? cost = null)
    {
        var created = Now.AddMinutes(-minutesAgo);
        return new InsightExecutionInput(
            Guid.NewGuid(),
            taskId ?? Guid.NewGuid(),
            title,
            status,
            created,
            created,
            created.AddSeconds(durationSec),
            Snapshot(outcome, compile, test, 0, baselineUnverified, flake, weakening, staleBase, failureReason, calls, tokens, cost));
    }
}
