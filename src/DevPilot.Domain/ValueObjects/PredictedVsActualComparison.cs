namespace DevPilot.Domain.ValueObjects;

public sealed class PredictedFileActionItem
{
    public string FilePath { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string EvidenceType { get; set; } = "Inferred";

    public bool IsUncertain { get; set; }
}

public sealed class ActualFileActionItem
{
    public string FilePath { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;
}

public sealed class PredictedVsActualComparison
{
    public List<PredictedFileActionItem> PredictedFiles { get; set; } = new();

    public List<ActualFileActionItem> ActualFiles { get; set; } = new();

    public List<string> MatchedFiles { get; set; } = new();

    public List<string> UnexpectedFiles { get; set; } = new();

    public List<string> MissingPredictedFiles { get; set; } = new();

    public List<string> ExpectedChecks { get; set; } = new();

    public List<string> ExecutedChecks { get; set; } = new();

    public bool AllExpectedChecksExecuted { get; set; }

    public List<string> DimensionObservations { get; set; } = new();
}
