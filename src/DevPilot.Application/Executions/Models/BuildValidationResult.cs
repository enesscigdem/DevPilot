namespace DevPilot.Application.Executions.Models;

/// <summary>
/// Result object for a build validation operation.
/// </summary>
public sealed record BuildValidationResult : ExecutionValidationResult
{
    public static BuildValidationResult FailResult(string errorMessage, string? targetPath = null) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        TargetPath = targetPath
    };
}
