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
    string BranchName);

public sealed record DeveloperAgentResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<string>? ModifiedFiles = null,
    string? RawAiResponse = null)
{
    public static DeveloperAgentResult Fail(string message) =>
        new(Success: false, ErrorMessage: message);

    public static DeveloperAgentResult Ok(IReadOnlyList<string> modifiedFiles, string? rawAiResponse = null) =>
        new(Success: true, ErrorMessage: null, ModifiedFiles: modifiedFiles, RawAiResponse: rawAiResponse);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FileEditAction
{
    Create,
    Modify
}

public sealed record StructuredEditPlan(
    [property: JsonPropertyName("files")] IReadOnlyList<FileEditSpec> Files);

public sealed record FileEditSpec(
    [property: JsonPropertyName("filePath")] string FilePath,
    [property: JsonPropertyName("action")] FileEditAction Action,
    [property: JsonPropertyName("newContent")] string? NewContent = null,
    [property: JsonPropertyName("searchReplaceEdits")] IReadOnlyList<SearchReplaceEdit>? SearchReplaceEdits = null);

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
