using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Dtos;

public sealed record ExecutionReviewStageStatusDto(
    string Status);

public sealed record ExecutionReviewFileDto(
    string Path,
    string ChangeType,
    int? Additions = null,
    int? Deletions = null);

public sealed record ExecutionReviewDto(
    Guid ExecutionId,
    Guid TaskId,
    string TaskTitle,
    string ExecutionStatus,
    string BranchName,
    int ChangedFileCount,
    IReadOnlyList<ExecutionReviewFileDto> ChangedFiles,
    string Diff,
    bool DiffTruncated,
    ExecutionReviewStageStatusDto Build,
    ExecutionReviewStageStatusDto Test,
    string ReviewStatus = "Pending",
    DateTime? DecidedAt = null,
    string? RejectionReason = null,
    string ChangeFingerprint = "",
    bool ApprovedSnapshotMatchesCurrent = true,
    bool CommitEligible = false,
    string CommitStatus = "None",
    string? CommitSha = null,
    DateTime? CommittedAt = null,
    string PushStatus = "None",
    string? RemoteBranchName = null,
    string? RemoteCommitSha = null,
    DateTime? PushedAt = null,
    bool CanRequestPush = false);
