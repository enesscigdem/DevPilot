using DevPilot.Application.Automation;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Automation;

public class AutomationGateTests
{
    private static AutomationPolicy Policy(Action<AutomationPolicy>? configure = null)
    {
        var policy = new AutomationPolicy { Level = AutomationLevel.AutoPr };
        configure?.Invoke(policy);
        return policy;
    }

    private static ExecutionReviewFileDto File(string path, int additions = 10, int deletions = 2) =>
        new(path, "Modified", additions, deletions);

    private static AutomationGateResult Delivery(
        AutomationPolicy policy,
        IReadOnlyList<ExecutionReviewFileDto>? files = null,
        ExecutionVerificationOutcome outcome = ExecutionVerificationOutcome.Verified,
        bool sensitive = false,
        bool visual = false) =>
        AutomationGate.EvaluateDelivery(
            policy, outcome, verdict: null, files ?? new[] { File("src/App/Service.cs") }, sensitive, visual);

    [Fact]
    public void Clean_verified_change_within_limits_passes()
    {
        Delivery(Policy()).Allowed.Should().BeTrue();
    }

    [Fact]
    public void No_new_regressions_is_accepted()
    {
        Delivery(Policy(), outcome: ExecutionVerificationOutcome.NoNewRegressions).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(ExecutionVerificationOutcome.PartiallyVerified)]
    [InlineData(ExecutionVerificationOutcome.VerificationUnavailable)]
    [InlineData(ExecutionVerificationOutcome.NeedsReview)]
    [InlineData(ExecutionVerificationOutcome.Failed)]
    [InlineData(ExecutionVerificationOutcome.Blocked)]
    public void Anything_short_of_verified_is_left_for_a_person(ExecutionVerificationOutcome outcome)
    {
        var result = Delivery(Policy(), outcome: outcome);

        result.Allowed.Should().BeFalse();
        result.Summary.Should().Contain(outcome.ToString());
    }

    [Fact]
    public void Sensitive_files_are_left_for_a_person_but_verified_ui_changes_are_not()
    {
        Delivery(Policy(), sensitive: true).Allowed.Should().BeFalse();
        Delivery(Policy(), visual: true).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Too_many_files_or_lines_is_left_for_a_person()
    {
        var policy = Policy(p => { p.MaxFilesChanged = 2; p.MaxLinesChanged = 50; });

        Delivery(policy, new[] { File("a.cs"), File("b.cs"), File("c.cs") }).Allowed.Should().BeFalse();
        Delivery(policy, new[] { File("a.cs", additions: 40, deletions: 20) }).Allowed.Should().BeFalse();
        Delivery(policy, new[] { File("a.cs", additions: 30, deletions: 20) }).Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_change_without_files_never_passes()
    {
        Delivery(Policy(), Array.Empty<ExecutionReviewFileDto>()).Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("src/DevPilot.Infrastructure/Migrations/20261005_Add.cs")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("src/Api/.env")]
    [InlineData("config/prod.env")]
    [InlineData("src/Api/appsettings.Production.json")]
    [InlineData("certs/site.pem")]
    public void Default_protected_paths_stop_automation(string path)
    {
        var result = Delivery(Policy(), new[] { File(path) });

        result.Allowed.Should().BeFalse();
        result.Summary.Should().Contain("protected");
    }

    [Theory]
    [InlineData("src/Api/Services/MigrationHelper.cs")]
    [InlineData("docs/github-workflows.md")]
    [InlineData("src/Api/appsettings.json")]
    public void Look_alike_paths_are_not_protected(string path)
    {
        Delivery(Policy(), new[] { File(path) }).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Protected_paths_match_windows_separators_and_ignore_case()
    {
        AutomationGate.FindProtectedPaths("**/Migrations/**", new[] { @"src\Infra\migrations\Init.cs" })
            .Should().ContainSingle();
    }

    [Fact]
    public void Single_star_does_not_cross_directories_but_double_star_does()
    {
        AutomationGate.FindProtectedPaths("src/*.cs", new[] { "src/a.cs", "src/deep/a.cs" })
            .Should().Equal("src/a.cs");
        AutomationGate.FindProtectedPaths("src/**/*.cs", new[] { "src/a.cs", "src/deep/a.cs" })
            .Should().BeEquivalentTo("src/a.cs", "src/deep/a.cs");
    }

    [Fact]
    public void Verdict_flags_block_delivery()
    {
        var weakened = Verdict(testWeakening: true);
        var stale = Verdict(staleBase: true);
        var files = new[] { File("a.cs") };

        AutomationGate.EvaluateDelivery(Policy(), ExecutionVerificationOutcome.Verified, weakened, files, false, false)
            .Allowed.Should().BeFalse();
        AutomationGate.EvaluateDelivery(Policy(), ExecutionVerificationOutcome.Verified, stale, files, false, false)
            .Allowed.Should().BeFalse();
    }

    [Fact]
    public void Several_problems_are_all_reported()
    {
        var result = Delivery(
            Policy(p => p.MaxFilesChanged = 1),
            new[] { File("a.cs"), File(".github/workflows/x.yml") },
            ExecutionVerificationOutcome.NeedsReview,
            sensitive: true);

        result.Reasons.Count.Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void Merge_is_ready_only_when_open_valid_and_ci_green()
    {
        Merge(Policy(), ExecutionCiStatus.Success).Readiness.Should().Be(AutomationMergeReadiness.Ready);
    }

    [Theory]
    [InlineData(ExecutionCiStatus.Pending)]
    [InlineData(ExecutionCiStatus.Unknown)]
    public void Merge_waits_while_ci_is_running(ExecutionCiStatus ci)
    {
        Merge(Policy(), ci).Readiness.Should().Be(AutomationMergeReadiness.Waiting);
    }

    [Fact]
    public void Failed_ci_blocks_the_merge()
    {
        Merge(Policy(), ExecutionCiStatus.Failure).Readiness.Should().Be(AutomationMergeReadiness.Blocked);
    }

    [Fact]
    public void No_checks_blocks_the_merge_unless_the_policy_allows_it()
    {
        Merge(Policy(), ExecutionCiStatus.NoChecks).Readiness.Should().Be(AutomationMergeReadiness.Blocked);
        Merge(Policy(p => p.RequireGreenCiForMerge = false), ExecutionCiStatus.NoChecks)
            .Readiness.Should().Be(AutomationMergeReadiness.Ready);
    }

    [Fact]
    public void A_changed_or_closed_pull_request_is_never_merged()
    {
        AutomationGate.EvaluateMerge(Policy(), ExecutionPullRequestRemoteState.Open,
                ExecutionPullRequestIntegrityStatus.HeadChanged, ExecutionCiStatus.Success)
            .Readiness.Should().Be(AutomationMergeReadiness.Blocked);
        AutomationGate.EvaluateMerge(Policy(), ExecutionPullRequestRemoteState.Closed,
                ExecutionPullRequestIntegrityStatus.Valid, ExecutionCiStatus.Success)
            .Readiness.Should().Be(AutomationMergeReadiness.Blocked);
    }

    [Fact]
    public void Ledger_explains_the_same_reason_only_once()
    {
        var ledger = new AutomationDecisionLedger();
        var id = Guid.NewGuid();

        ledger.ShouldRecord(id, "Review:too big").Should().BeTrue();
        ledger.ShouldRecord(id, "Review:too big").Should().BeFalse();
        ledger.ShouldRecord(id, "Review:CI failed").Should().BeTrue();
        ledger.ShouldRecord(Guid.NewGuid(), "Review:too big").Should().BeTrue();
    }

    [Fact]
    public void Build_only_change_in_a_repository_without_tests_is_delivered()
    {
        var result = AutomationGate.EvaluateDelivery(
            Policy(), ExecutionVerificationOutcome.PartiallyVerified, Verdict(buildOnly: true),
            new[] { File("src/App.tsx") }, hasSensitiveFiles: false, visualReviewRequired: false);

        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Partial_verification_for_any_other_reason_still_waits_for_a_person()
    {
        var result = AutomationGate.EvaluateDelivery(
            Policy(), ExecutionVerificationOutcome.PartiallyVerified, Verdict(buildOnly: false),
            new[] { File("src/App.tsx") }, hasSensitiveFiles: false, visualReviewRequired: false);

        result.Allowed.Should().BeFalse();
        result.Summary.Should().Contain("PartiallyVerified");
    }

    [Fact]
    public void The_build_only_exception_never_lets_a_needs_review_outcome_through()
    {
        var result = AutomationGate.EvaluateDelivery(
            Policy(), ExecutionVerificationOutcome.NeedsReview, Verdict(buildOnly: true),
            new[] { File("src/App.tsx") }, hasSensitiveFiles: false, visualReviewRequired: false);

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public void A_build_only_change_still_obeys_every_other_safety_gate()
    {
        var result = AutomationGate.EvaluateDelivery(
            Policy(), ExecutionVerificationOutcome.PartiallyVerified, Verdict(buildOnly: true, staleBase: true),
            new[] { File(".github/workflows/ci.yml"), File("src/App.tsx") },
            hasSensitiveFiles: true, visualReviewRequired: true);

        result.Allowed.Should().BeFalse();
        result.Reasons.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    private static AutomationMergeDecision Merge(AutomationPolicy policy, ExecutionCiStatus ci) =>
        AutomationGate.EvaluateMerge(
            policy, ExecutionPullRequestRemoteState.Open, ExecutionPullRequestIntegrityStatus.Valid, ci);

    private static ExecutionVerdictDto Verdict(bool testWeakening = false, bool staleBase = false, bool buildOnly = false) =>
        new(
            Outcome: "Verified",
            Severity: "success",
            Headline: "ok",
            RecommendedAction: null,
            Findings: Array.Empty<VerdictFindingDto>(),
            BaselineUnverified: false,
            FlakeConfirmed: false,
            TestWeakeningSuspected: testWeakening,
            StaleBase: staleBase,
            CompileRepairRounds: 0,
            TestRepairRounds: 0,
            CompactRetries: 0,
            ApplicabilityRepairs: 0,
            ChecksNotRun: Array.Empty<string>(),
            BuildOnlyNoTestSuite: buildOnly);
}
