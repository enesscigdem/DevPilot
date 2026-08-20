using DevPilot.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Tasks;

public sealed class TaskImpactActionStateTests
{
    public enum ActionKind
    {
        ActiveExecution,
        SyncingExecution,
        AwaitingApproval,
        Approved,
        Failed,
        Rejected,
        None
    }

    public record ActionState(
        ActionKind Kind,
        bool CanStart,
        bool CanRetry,
        bool CanApprove,
        Guid? ActiveExecutionId,
        string? Message);

    public enum ImpactLifecycle
    {
        Idle,
        Analyzing,
        Succeeded,
        Failed
    }

    public record ImpactLifecycleState(
        ImpactLifecycle Lifecycle,
        string StatusTone,
        string StatusLabel,
        bool CanRun,
        bool CanRetry,
        bool CanApprove,
        bool IsAnalyzing,
        bool IsSucceeded,
        bool IsFailed,
        int ElapsedSeconds,
        string? DurationFormatted,
        string? SanitizedErrorMessage);

    public static string FormatDurationSeconds(int totalSeconds)
    {
        if (totalSeconds < 0) return "0s";
        if (totalSeconds < 60) return $"{totalSeconds}s";
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return seconds > 0 ? $"{minutes}m {seconds}s" : $"{minutes}m";
    }

    public static string SanitizeErrorMessage(string? rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError)) return "Impact analysis failed.";
        var sanitized = rawError.Trim();
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"bearer\s+[a-zA-Z0-9_\-\.]+",
            "Bearer [REDACTED]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"(api[_-]?key|secret|password)\s*[:=]\s*[""']?[^""'\s]+[""']?",
            "$1=[REDACTED]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return sanitized;
    }

    public static ImpactLifecycleState DeriveImpactLifecycle(
        DevelopmentTaskStatus? taskStatus,
        ImpactAnalysisStatus? analysisStatus,
        DateTime? analysisCreatedAt,
        DateTime? analysisCompletedAt,
        string? errorMessage,
        bool hasStructuredResult,
        bool hasActiveExecution,
        DateTime nowUtc)
    {
        var isAnalyzing = analysisStatus == ImpactAnalysisStatus.InProgress ||
                          (taskStatus == DevelopmentTaskStatus.Analyzing &&
                           analysisStatus != ImpactAnalysisStatus.Completed &&
                           analysisStatus != ImpactAnalysisStatus.Failed);

        if (isAnalyzing)
        {
            var elapsed = analysisCreatedAt.HasValue
                ? (int)Math.Max(0, (nowUtc - analysisCreatedAt.Value).TotalSeconds)
                : 0;

            return new ImpactLifecycleState(
                ImpactLifecycle.Analyzing,
                StatusTone: "blue",
                StatusLabel: "Analyzing",
                CanRun: false,
                CanRetry: false,
                CanApprove: false,
                IsAnalyzing: true,
                IsSucceeded: false,
                IsFailed: false,
                ElapsedSeconds: elapsed,
                DurationFormatted: null,
                SanitizedErrorMessage: null);
        }

        if (analysisStatus == ImpactAnalysisStatus.Completed && hasStructuredResult)
        {
            string? durationStr = null;
            if (analysisCreatedAt.HasValue && analysisCompletedAt.HasValue && analysisCompletedAt.Value >= analysisCreatedAt.Value)
            {
                durationStr = FormatDurationSeconds((int)(analysisCompletedAt.Value - analysisCreatedAt.Value).TotalSeconds);
            }

            return new ImpactLifecycleState(
                ImpactLifecycle.Succeeded,
                StatusTone: "amber",
                StatusLabel: "Awaiting approval",
                CanRun: false,
                CanRetry: false,
                CanApprove: taskStatus == DevelopmentTaskStatus.AwaitingApproval,
                IsAnalyzing: false,
                IsSucceeded: true,
                IsFailed: false,
                ElapsedSeconds: 0,
                DurationFormatted: durationStr,
                SanitizedErrorMessage: null);
        }

        if (analysisStatus == ImpactAnalysisStatus.Failed || (taskStatus == DevelopmentTaskStatus.Failed && !isAnalyzing))
        {
            string? durationStr = null;
            if (analysisCreatedAt.HasValue && analysisCompletedAt.HasValue && analysisCompletedAt.Value >= analysisCreatedAt.Value)
            {
                durationStr = FormatDurationSeconds((int)(analysisCompletedAt.Value - analysisCreatedAt.Value).TotalSeconds);
            }

            return new ImpactLifecycleState(
                ImpactLifecycle.Failed,
                StatusTone: "red",
                StatusLabel: "Failed",
                CanRun: false,
                CanRetry: !hasActiveExecution,
                CanApprove: false,
                IsAnalyzing: false,
                IsSucceeded: false,
                IsFailed: true,
                ElapsedSeconds: 0,
                DurationFormatted: durationStr,
                SanitizedErrorMessage: SanitizeErrorMessage(errorMessage));
        }

        return new ImpactLifecycleState(
            ImpactLifecycle.Idle,
            StatusTone: "neutral",
            StatusLabel: "Draft",
            CanRun: true,
            CanRetry: false,
            CanApprove: false,
            IsAnalyzing: false,
            IsSucceeded: false,
            IsFailed: false,
            ElapsedSeconds: 0,
            DurationFormatted: null,
            SanitizedErrorMessage: null);
    }

    public static ActionState DeriveActionState(
        DevelopmentTaskStatus? taskStatus,
        (Guid Id, TaskExecutionStatus Status)? activeExecution)
    {
        if (activeExecution.HasValue)
        {
            return new ActionState(
                ActionKind.ActiveExecution,
                CanStart: false,
                CanRetry: false,
                CanApprove: false,
                ActiveExecutionId: activeExecution.Value.Id,
                Message: activeExecution.Value.Status == TaskExecutionStatus.Running
                    ? "Agent is currently executing the task."
                    : "Execution is queued and will begin processing shortly.");
        }

        if (taskStatus == DevelopmentTaskStatus.Executing)
        {
            return new ActionState(
                ActionKind.SyncingExecution,
                CanStart: false,
                CanRetry: false,
                CanApprove: false,
                ActiveExecutionId: null,
                Message: "Execution state is syncing with the server…");
        }

        if (taskStatus == DevelopmentTaskStatus.Approved)
        {
            return new ActionState(
                ActionKind.Approved,
                CanStart: true,
                CanRetry: false,
                CanApprove: false,
                ActiveExecutionId: null,
                Message: null);
        }

        if (taskStatus == DevelopmentTaskStatus.Failed)
        {
            return new ActionState(
                ActionKind.Failed,
                CanStart: false,
                CanRetry: true,
                CanApprove: false,
                ActiveExecutionId: null,
                Message: null);
        }

        if (taskStatus == DevelopmentTaskStatus.AwaitingApproval)
        {
            return new ActionState(
                ActionKind.AwaitingApproval,
                CanStart: false,
                CanRetry: false,
                CanApprove: true,
                ActiveExecutionId: null,
                Message: null);
        }

        if (taskStatus == DevelopmentTaskStatus.Rejected)
        {
            return new ActionState(
                ActionKind.Rejected,
                CanStart: false,
                CanRetry: false,
                CanApprove: false,
                ActiveExecutionId: null,
                Message: null);
        }

        return new ActionState(
            ActionKind.None,
            CanStart: false,
            CanRetry: false,
            CanApprove: false,
            ActiveExecutionId: null,
            Message: null);
    }

    [Fact]
    public void TaskStatusExecuting_WhenActiveExecutionNull_MakesStartAndRetryUnavailable_AndHasNoLiveExecutionId()
    {
        var state = DeriveActionState(DevelopmentTaskStatus.Executing, null);

        state.Kind.Should().Be(ActionKind.SyncingExecution);
        state.CanStart.Should().BeFalse();
        state.CanRetry.Should().BeFalse();
        state.ActiveExecutionId.Should().BeNull();
        state.Message.Should().Contain("syncing");
    }

    [Fact]
    public void TaskStatusFailed_WhenActiveRunningExecutionExists_MakesRetryUnavailable_AndUsesActualExecutionId()
    {
        var runningExecId = Guid.NewGuid();
        var state = DeriveActionState(DevelopmentTaskStatus.Failed, (runningExecId, TaskExecutionStatus.Running));

        state.Kind.Should().Be(ActionKind.ActiveExecution);
        state.CanStart.Should().BeFalse();
        state.CanRetry.Should().BeFalse();
        state.ActiveExecutionId.Should().Be(runningExecId);
        state.Message.Should().Contain("executing");
    }

    [Fact]
    public void TaskStatusApproved_WhenActiveExecutionNull_MakesStartAvailable()
    {
        var state = DeriveActionState(DevelopmentTaskStatus.Approved, null);

        state.Kind.Should().Be(ActionKind.Approved);
        state.CanStart.Should().BeTrue();
        state.CanRetry.Should().BeFalse();
        state.ActiveExecutionId.Should().BeNull();
    }

    [Fact]
    public void DeriveImpactLifecycle_WhenAnalysisInProgress_ReturnsAnalyzingWithElapsed()
    {
        var now = DateTime.UtcNow;
        var createdAt = now.AddSeconds(-25);

        var state = DeriveImpactLifecycle(
            DevelopmentTaskStatus.Analyzing,
            ImpactAnalysisStatus.InProgress,
            createdAt,
            null,
            null,
            false,
            false,
            now);

        state.Lifecycle.Should().Be(ImpactLifecycle.Analyzing);
        state.IsAnalyzing.Should().BeTrue();
        state.CanRun.Should().BeFalse();
        state.CanRetry.Should().BeFalse();
        state.ElapsedSeconds.Should().Be(25);
    }

    [Fact]
    public void DeriveImpactLifecycle_WhenAnalysisCompleted_ReturnsSucceededWithDuration()
    {
        var now = DateTime.UtcNow;
        var createdAt = now.AddSeconds(-42);
        var completedAt = now.AddSeconds(-10); // duration = 32s

        var state = DeriveImpactLifecycle(
            DevelopmentTaskStatus.AwaitingApproval,
            ImpactAnalysisStatus.Completed,
            createdAt,
            completedAt,
            null,
            true,
            false,
            now);

        state.Lifecycle.Should().Be(ImpactLifecycle.Succeeded);
        state.IsSucceeded.Should().BeTrue();
        state.CanRun.Should().BeFalse();
        state.CanApprove.Should().BeTrue();
        state.DurationFormatted.Should().Be("32s");
    }

    [Fact]
    public void DeriveImpactLifecycle_WhenAnalysisFailed_ReturnsFailedWithSanitizedErrorAndRetry()
    {
        var now = DateTime.UtcNow;
        var createdAt = now.AddSeconds(-30);
        var completedAt = now.AddSeconds(-12);

        var state = DeriveImpactLifecycle(
            DevelopmentTaskStatus.Failed,
            ImpactAnalysisStatus.Failed,
            createdAt,
            completedAt,
            "Invalid token: Bearer abc123def456! Failed to call API.",
            false,
            false,
            now);

        state.Lifecycle.Should().Be(ImpactLifecycle.Failed);
        state.IsFailed.Should().BeTrue();
        state.CanRun.Should().BeFalse();
        state.CanRetry.Should().BeTrue();
        state.DurationFormatted.Should().Be("18s");
        state.SanitizedErrorMessage.Should().Contain("Bearer [REDACTED]");
        state.SanitizedErrorMessage.Should().NotContain("abc123def456");
    }

    [Theory]
    [InlineData(12, "12s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m")]
    [InlineData(75, "1m 15s")]
    [InlineData(130, "2m 10s")]
    public void FormatDurationSeconds_FormatsCorrectly(int totalSeconds, string expected)
    {
        FormatDurationSeconds(totalSeconds).Should().Be(expected);
    }
}
