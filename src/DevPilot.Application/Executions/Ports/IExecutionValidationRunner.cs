using DevPilot.Application.Executions.Models;

namespace DevPilot.Application.Executions.Ports;

/// <summary>
/// Legacy .NET validation contract retained for the existing .NET-specific adapter and tests.
/// The generic execution processor depends on <see cref="IRepositoryCheckRunner"/> instead.
/// </summary>
/// <remarks>
/// TRUST BOUNDARY NOTICE: MSBuild project files (.csproj / .sln) can execute custom build logic
/// and are NOT a security sandbox. This runner is foundation-only for trusted worktree validation.
/// Future pipeline integration for untrusted repositories will require container/sandbox isolation.
/// </remarks>
public interface IExecutionValidationRunner
{
    Task<BuildValidationResult> ValidateBuildAsync(
        ExecutionValidationRequest request,
        CancellationToken cancellationToken = default);

    Task<TestValidationResult> ValidateTestAsync(
        ExecutionValidationRequest request,
        CancellationToken cancellationToken = default);
}
