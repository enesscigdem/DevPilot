using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;

namespace DevPilot.Tests.Executions;

internal sealed class TestRepositoryCheckRunnerAdapter : IRepositoryCheckRunner
{
    private static readonly RepositoryCheck BuildCheck = new(
        "dotnet:build:test",
        ".NET build",
        RepositoryCheckKind.Build,
        "dotnet",
        "dotnet",
        new[] { "build", "Test.sln" },
        ".",
        true,
        TimeSpan.FromMinutes(5),
        RepositoryCheckSource.DotNetManifest,
        "Test.sln",
        Order: 100);

    private static readonly RepositoryCheck TestCheck = new(
        "dotnet:test:test",
        ".NET tests",
        RepositoryCheckKind.Test,
        "dotnet",
        "dotnet",
        new[] { "test", "Test.Tests.csproj" },
        ".",
        true,
        TimeSpan.FromMinutes(10),
        RepositoryCheckSource.DotNetManifest,
        "Test.Tests.csproj",
        SupportsSkipBuild: true,
        SupportsTargetedTest: true,
        Order: 400);

    private readonly IExecutionValidationRunner _inner;

    public TestRepositoryCheckRunnerAdapter(IExecutionValidationRunner inner)
    {
        _inner = inner;
    }

    public Task<RepositoryProfile> DiscoverAsync(
        RepositoryPreflightRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new RepositoryProfile(
            RepositoryVerificationState.Configured,
            new[] { "dotnet" },
            new[] { BuildCheck, TestCheck }));

    public async Task<RepositoryCheckResult> ExecuteAsync(
        RepositoryCheckExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var legacyRequest = new ExecutionValidationRequest(
            request.WorkspacePath,
            request.BranchName,
            TargetPath: null,
            Timeout: request.Check.Timeout,
            SkipBuild: request.SkipBuild,
            TestFilter: request.TestFilter);
        ExecutionValidationResult result = request.Check.Kind == RepositoryCheckKind.Test
            ? await _inner.ValidateTestAsync(legacyRequest, cancellationToken).ConfigureAwait(false)
            : await _inner.ValidateBuildAsync(legacyRequest, cancellationToken).ConfigureAwait(false);
        var infrastructureFailure = result.IsTimedOut || (result.ExitCode.HasValue && result.ExitCode.Value < 0);

        return new RepositoryCheckResult
        {
            CheckId = request.Check.Id,
            CheckDisplayName = request.Check.DisplayName,
            CheckKind = request.Check.Kind,
            FailureCategory = result.Success
                ? RepositoryCheckFailureCategory.None
                : infrastructureFailure
                    ? RepositoryCheckFailureCategory.InfrastructureFailure
                    : RepositoryCheckFailureCategory.VerificationFailure,
            Success = result.Success,
            ExitCode = result.ExitCode,
            ErrorMessage = result.ErrorMessage,
            StartTime = result.StartTime,
            CompletionTime = result.CompletionTime,
            Duration = result.Duration,
            StdOut = result.StdOut,
            StdErr = result.StdErr,
            IsTruncated = result.IsTruncated,
            IsTimedOut = result.IsTimedOut,
            TargetPath = result.TargetPath
        };
    }
}
