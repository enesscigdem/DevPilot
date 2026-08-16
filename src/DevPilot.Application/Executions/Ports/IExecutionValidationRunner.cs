using DevPilot.Application.Executions.Models;

namespace DevPilot.Application.Executions.Ports;

/// <summary>
/// Execution validation runner interface for running approved build and test operations
/// exclusively inside an execution Git worktree.
/// </summary>
/// <remarks>
/// TRUST BOUNDARY NOTICE: MSBuild project files (.csproj / .sln) can execute custom build logic
/// and are NOT a security sandbox. This runner is foundation-only for trusted worktree validation.
/// Future pipeline integration for untrusted repositories will require container/sandbox isolation.
/// </remarks>
public interface IExecutionValidationRunner
{
    /// <summary>
    /// Validates and executes a build operation inside the specified execution workspace.
    /// </summary>
    Task<BuildValidationResult> ValidateBuildAsync(
        ExecutionValidationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and executes a test operation inside the specified execution workspace.
    /// </summary>
    Task<TestValidationResult> ValidateTestAsync(
        ExecutionValidationRequest request,
        CancellationToken cancellationToken = default);
}
