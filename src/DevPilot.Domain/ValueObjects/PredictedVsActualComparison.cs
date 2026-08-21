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

public sealed class DatabasePredictedVsActualComparison
{
    public string Status { get; set; } = "NotApplicable";

    public bool PredictedMigrationExpected { get; set; }

    public bool ActualMigrationCreated { get; set; }

    public List<DatabaseChange> PredictedChanges { get; set; } = new();

    public List<DatabaseChange> ActualChanges { get; set; } = new();

    public List<DatabaseChange> MatchedChanges { get; set; } = new();

    public List<DatabaseChange> UnexpectedChanges { get; set; } = new();

    public List<DatabaseChange> MissingPredictedChanges { get; set; } = new();

    public List<string> Observations { get; set; } = new();

    public bool HasDestructiveOperations { get; set; }

    public List<string> DestructiveWarnings { get; set; } = new();
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

    public DatabasePredictedVsActualComparison? DatabaseImpact { get; set; }
}
