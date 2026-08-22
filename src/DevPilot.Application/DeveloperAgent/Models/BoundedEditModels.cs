using System.Text.Json.Serialization;

namespace DevPilot.Application.DeveloperAgent.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BoundedEditOperationType
{
    Replace,
    InsertBefore,
    InsertAfter,
    Delete
}

public sealed record BoundedTargetSpan(
    string TargetId,
    int StartOffset,
    int Length,
    string Text,
    int StartLine,
    int EndLine,
    IReadOnlyList<BoundedEditOperationType>? AllowedOperations = null,
    string? Label = null)
{
    [JsonIgnore]
    public int EndOffset => StartOffset + Length;
}

public sealed record BoundedEditContext(
    string FilePath,
    string ExpectedHash,
    IReadOnlyList<BoundedTargetSpan> Targets,
    string FormattedContext);

public sealed record BoundedEditOperation
{
    [JsonPropertyName("type")]
    public BoundedEditOperationType Type { get; init; }

    [JsonPropertyName("targetId")]
    public string? TargetId { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    // Legacy fields kept for backward compatibility if needed
    [JsonPropertyName("oldText")]
    public string? OldText { get; init; }

    [JsonPropertyName("newText")]
    public string? NewText { get; init; }

    [JsonPropertyName("anchor")]
    public string? Anchor { get; init; }

    [JsonIgnore]
    public string? TargetText => TargetId ?? OldText ?? Anchor;

    [JsonIgnore]
    public string? ReplacementText => Content ?? NewText;

    public BoundedEditOperation() { }

    public BoundedEditOperation(BoundedEditOperationType type, string? targetIdOrOldText = null, string? contentOrNewText = null)
    {
        Type = type;
        if (targetIdOrOldText != null && targetIdOrOldText.StartsWith("T", StringComparison.OrdinalIgnoreCase) && targetIdOrOldText.Length <= 6 && !targetIdOrOldText.Contains('\n') && !targetIdOrOldText.Contains(' '))
        {
            TargetId = targetIdOrOldText;
        }
        OldText = targetIdOrOldText;
        Anchor = targetIdOrOldText;
        NewText = contentOrNewText;
        Content = contentOrNewText;
    }

    public static BoundedEditOperation ReplaceTarget(string targetId, string content) =>
        new()
        {
            Type = BoundedEditOperationType.Replace,
            TargetId = targetId,
            Content = content,
            NewText = content
        };

    public static BoundedEditOperation InsertBeforeTarget(string targetId, string content) =>
        new()
        {
            Type = BoundedEditOperationType.InsertBefore,
            TargetId = targetId,
            Content = content,
            NewText = content
        };

    public static BoundedEditOperation InsertAfterTarget(string targetId, string content) =>
        new()
        {
            Type = BoundedEditOperationType.InsertAfter,
            TargetId = targetId,
            Content = content,
            NewText = content
        };

    public static BoundedEditOperation DeleteTarget(string targetId) =>
        new()
        {
            Type = BoundedEditOperationType.Delete,
            TargetId = targetId
        };

    public static BoundedEditOperation Replace(string oldText, string newText) =>
        new()
        {
            Type = BoundedEditOperationType.Replace,
            OldText = oldText,
            NewText = newText,
            Anchor = oldText,
            Content = newText
        };

    public static BoundedEditOperation InsertBefore(string anchor, string content) =>
        new()
        {
            Type = BoundedEditOperationType.InsertBefore,
            Anchor = anchor,
            OldText = anchor,
            Content = content,
            NewText = content
        };

    public static BoundedEditOperation InsertAfter(string anchor, string content) =>
        new()
        {
            Type = BoundedEditOperationType.InsertAfter,
            Anchor = anchor,
            OldText = anchor,
            Content = content,
            NewText = content
        };

    public static BoundedEditOperation Delete(string oldText) =>
        new()
        {
            Type = BoundedEditOperationType.Delete,
            OldText = oldText,
            Anchor = oldText
        };
}

public sealed record BoundedFileEdit(
    [property: JsonPropertyName("filePath")] string FilePath,
    [property: JsonPropertyName("expectedFileHash")] string? ExpectedFileHash = null,
    [property: JsonPropertyName("operations")] IReadOnlyList<BoundedEditOperation>? Operations = null,
    [property: JsonPropertyName("expectedHash")] string? ExpectedHash = null)
{
    [JsonIgnore]
    public string? EffectiveExpectedHash => ExpectedFileHash ?? ExpectedHash;
}

public sealed record BoundedEditPlan(
    [property: JsonPropertyName("files")] IReadOnlyList<BoundedFileEdit> Files);

public sealed record BoundedEditLimits(
    int MaxOperationsPerFile = 50,
    int MaxAggregateContentChars = 100_000,
    int MaxIndividualOperationChars = 50_000)
{
    public static BoundedEditLimits Default { get; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BoundedEditFailureReason
{
    None,
    StaleSource,
    AnchorNotFound,
    AmbiguousAnchor,
    InvalidOperation,
    UnauthorizedTarget,
    PathOutsideWorktree,
    TargetNotFound,
    BinaryFile,
    MaxOperationsExceeded,
    MaxContentSizeExceeded,
    ApplyConflict
}

public sealed record BoundedEditResult(
    bool Success,
    BoundedEditFailureReason FailureReason,
    string? ErrorMessage,
    string? ModifiedContent = null,
    int FailedOperationIndex = -1,
    int TotalOperations = 0,
    BoundedEditOperationType? FailedOperationType = null,
    string? FailedAnchor = null,
    string? FailedReplacement = null,
    int MatchCount = 0,
    string? SurroundingContext = null)
{
    public static BoundedEditResult Ok(string modifiedContent, int totalOperations) =>
        new(Success: true,
            FailureReason: BoundedEditFailureReason.None,
            ErrorMessage: null,
            ModifiedContent: modifiedContent,
            FailedOperationIndex: -1,
            TotalOperations: totalOperations);

    public static BoundedEditResult Fail(
        BoundedEditFailureReason reason,
        string errorMessage,
        int failedOperationIndex = -1,
        int totalOperations = 0,
        BoundedEditOperationType? failedOperationType = null,
        string? failedAnchor = null,
        string? failedReplacement = null,
        int matchCount = 0,
        string? surroundingContext = null) =>
        new(Success: false,
            FailureReason: reason,
            ErrorMessage: errorMessage,
            ModifiedContent: null,
            FailedOperationIndex: failedOperationIndex,
            TotalOperations: totalOperations,
            FailedOperationType: failedOperationType,
            FailedAnchor: failedAnchor,
            FailedReplacement: failedReplacement,
            MatchCount: matchCount,
            SurroundingContext: surroundingContext);
}

public sealed record BoundedEditPlanResult(
    bool Success,
    BoundedEditFailureReason FailureReason,
    string? ErrorMessage,
    IReadOnlyList<string>? ModifiedFiles = null,
    string? FailedFilePath = null,
    BoundedEditResult? FileEditResult = null)
{
    public static BoundedEditPlanResult Ok(IReadOnlyList<string> modifiedFiles) =>
        new(Success: true,
            FailureReason: BoundedEditFailureReason.None,
            ErrorMessage: null,
            ModifiedFiles: modifiedFiles);

    public static BoundedEditPlanResult Fail(
        BoundedEditFailureReason reason,
        string errorMessage,
        string? failedFilePath = null,
        BoundedEditResult? fileEditResult = null) =>
        new(Success: false,
            FailureReason: reason,
            ErrorMessage: errorMessage,
            ModifiedFiles: null,
            FailedFilePath: failedFilePath,
            FileEditResult: fileEditResult);
}
