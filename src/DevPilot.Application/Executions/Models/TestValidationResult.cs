namespace DevPilot.Application.Executions.Models;

/// <summary>
/// Result object for a test validation operation.
/// </summary>
public sealed record TestValidationResult : ExecutionValidationResult
{
    public static TestValidationResult FailResult(string errorMessage, string? targetPath = null) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        TargetPath = targetPath
    };
}
