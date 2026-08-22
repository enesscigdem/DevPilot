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

public sealed record BoundedEditOperation
{
    [JsonPropertyName("type")]
    public BoundedEditOperationType Type { get; init; }

    [JsonPropertyName("oldText")]
    public string? OldText { get; init; }

    [JsonPropertyName("newText")]
    public string? NewText { get; init; }

    [JsonPropertyName("anchor")]
    public string? Anchor { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonIgnore]
    public string? TargetText => OldText ?? Anchor;

    [JsonIgnore]
    public string? ReplacementText => NewText ?? Content;

    public BoundedEditOperation() { }

    public BoundedEditOperation(BoundedEditOperationType type, string? oldTextOrAnchor = null, string? newTextOrContent = null)
    {
        Type = type;
        OldText = oldTextOrAnchor;
        Anchor = oldTextOrAnchor;
        NewText = newTextOrContent;
        Content = newTextOrContent;
    }

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
