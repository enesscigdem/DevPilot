using System.Text.Json.Serialization;

namespace DevPilot.Application.DeveloperAgent.Models;

public sealed record DeveloperAgentRequest(
    Guid TaskId,
    Guid ExecutionId,
    string TaskTitle,
    string TaskDescription,
    string? AcceptanceCriteria,
    string ImpactAnalysisSummary,
    string ProposedPlan,
    IReadOnlyList<string> ImpactedFilePaths,
    string WorkspacePath,
    string BranchName,
    IReadOnlyList<ImpactedFileDetail>? ImpactedFiles = null,
    string? Model = null,
    IReadOnlyList<string>? ChangeDimensions = null,
    IReadOnlyList<string>? ExpectedChecks = null,
    IReadOnlyList<string>? Unknowns = null,
    bool IsVerificationRepair = false)
{
    [JsonIgnore]
    public bool IsVerificationRepairRequest =>
        IsVerificationRepair ||
        TaskTitle.StartsWith("Repair ", StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(ImpactAnalysisSummary) && (ImpactAnalysisSummary.StartsWith("Repository check repair", StringComparison.OrdinalIgnoreCase) || ImpactAnalysisSummary.StartsWith("Test repair", StringComparison.OrdinalIgnoreCase))) ||
        (!string.IsNullOrEmpty(TaskDescription) && (TaskDescription.Contains("Fix the following authoritative repository check failure", StringComparison.OrdinalIgnoreCase) || TaskDescription.Contains("Fix the authoritative failing test evidence", StringComparison.OrdinalIgnoreCase)));
}

public sealed record ImpactedFileDetail(
    string FilePath,
    string? ChangeType = null,
    string? Reason = null,
    string? EvidenceType = null,
    bool IsUncertain = false);

public sealed record DeveloperAgentResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<string>? ModifiedFiles = null,
    string? RawAiResponse = null,
    string? Model = null)
{
    public static DeveloperAgentResult Fail(string message, string? model = null) =>
        new(Success: false, ErrorMessage: message, Model: model);

    public static DeveloperAgentResult Ok(IReadOnlyList<string> modifiedFiles, string? rawAiResponse = null, string? model = null) =>
        new(Success: true, ErrorMessage: null, ModifiedFiles: modifiedFiles, RawAiResponse: rawAiResponse, Model: model);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FileEditAction
{
    Create,
    Modify
}

public sealed record EditManifest(
    [property: JsonPropertyName("files")] IReadOnlyList<ManifestFileEntry> Files);

public sealed record ManifestFileEntry(
    [property: JsonPropertyName("filePath")] string FilePath,
    [property: JsonPropertyName("action")] FileEditAction Action,
    [property: JsonPropertyName("purpose")] string? Purpose = null,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<string>? Dependencies = null);

public sealed record StructuredEditPlan(
    [property: JsonPropertyName("files")] IReadOnlyList<FileEditSpec> Files);

public sealed record FileEditSpec(
    [property: JsonPropertyName("filePath")] string FilePath,
    [property: JsonPropertyName("action")] FileEditAction Action,
    [property: JsonPropertyName("newContent")] string? NewContent = null,
    [property: JsonPropertyName("searchReplaceEdits")] IReadOnlyList<SearchReplaceEdit>? SearchReplaceEdits = null,
    [property: JsonPropertyName("targetContentHash")] string? TargetContentHash = null,
    [property: JsonPropertyName("operations")] IReadOnlyList<BoundedEditOperation>? Operations = null,
    [property: JsonPropertyName("expectedHash")] string? ExpectedHash = null);

public sealed record SearchReplaceEdit(
    [property: JsonPropertyName("search")] string Search,
    [property: JsonPropertyName("replace")] string Replace);

public sealed record ContextLimits(
    int MaxFileCount = 20,
    long MaxFileSizeBytes = 100 * 1024, // 100 KB
    long MaxTotalContentSizeBytes = 500 * 1024) // 500 KB
{
    public static ContextLimits Default { get; } = new();
}

public sealed record EditApplicabilityResult(
    bool Success,
    string? ErrorMessage,
    string? ModifiedContent,
    int FailedEditIndex = -1,
    int TotalEdits = 0,
    string? FailedSearch = null,
    string? FailedReplace = null,
    int MatchCount = 0,
    string? SurroundingContext = null)
{
    public static EditApplicabilityResult Ok(string modifiedContent, int totalEdits) =>
        new(true, null, modifiedContent, -1, totalEdits);

    public static EditApplicabilityResult Fail(
        string errorMessage,
        int failedEditIndex,
        int totalEdits,
        string? failedSearch = null,
        string? failedReplace = null,
        int matchCount = 0,
        string? surroundingContext = null) =>
        new(false, errorMessage, null, failedEditIndex, totalEdits, failedSearch, failedReplace, matchCount, surroundingContext);
}
