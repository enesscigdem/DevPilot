using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class RepositoryNativeCheckRunnerTests : IDisposable
{
    private readonly string _workspace;
    private readonly TestWorkspaceManager _workspaceManager = new();
    private readonly RecordingProcessRunner _processRunner = new();
    private readonly RepositoryNativeCheckRunner _runner;

    public RepositoryNativeCheckRunnerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"devpilot-native-checks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        _runner = new RepositoryNativeCheckRunner(
            _workspaceManager,
            _processRunner,
            NullLogger<RepositoryNativeCheckRunner>.Instance);
    }

    [Fact]
    public void ExecutionProcessor_DependsOnGenericRepositoryPorts_NotDotNetValidation()
    {
        var parameters = typeof(GitWorkspaceExecutionProcessor)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToList();

        parameters.Should().Contain(typeof(IRepositoryCheckRunner));
        parameters.Should().Contain(typeof(IRepositoryRepairContextProvider));
        parameters.Should().NotContain(typeof(IExecutionValidationRunner));
    }

    [Fact]
    public async Task DotNetRepository_MapsToNativeBuildAndTestChecks()
    {
        WriteFile("DevPilot.sln", string.Empty);
        WriteFile("tests/DevPilot.Tests/DevPilot.Tests.csproj", "<Project />");

        var profile = await DiscoverAsync();

        profile.State.Should().Be(RepositoryVerificationState.Configured);
        profile.Ecosystems.Should().ContainSingle().Which.Should().Be("dotnet");
        profile.Checks.Should().ContainSingle(check =>
            check.Kind == RepositoryCheckKind.Build &&
            check.Executable == "dotnet" &&
            check.Arguments.SequenceEqual(new[] { "build", "DevPilot.sln" }));
        profile.Checks.Should().ContainSingle(check =>
            check.Kind == RepositoryCheckKind.Test &&
            check.Executable == "dotnet" &&
            check.Arguments.SequenceEqual(new[] { "test", "tests/DevPilot.Tests/DevPilot.Tests.csproj" }) &&
            check.SupportsSkipBuild &&
            check.SupportsTargetedTest);
    }

    [Fact]
    public async Task SolutionlessMultiProjectRepository_WithUniqueProjectReferenceRoot_ResolvesThatProject()
    {
        WriteFile("src/Entry/Entry.csproj", ProjectFile("../Feature/Feature.csproj", "../Data/Data.csproj"));
        WriteFile("src/Feature/Feature.csproj", ProjectFile("../Core/Core.csproj"));
        WriteFile("src/Data/Data.csproj", ProjectFile("../Core/Core.csproj"));
        WriteFile("src/Core/Core.csproj", ProjectFile());

        var profile = await DiscoverAsync();

        profile.State.Should().Be(RepositoryVerificationState.Configured);
        profile.HasUnresolvedVerification.Should().BeFalse();
        var build = profile.Checks.Single(check => check.Kind == RepositoryCheckKind.Build);
        build.Arguments.Should().Equal("build", "src/Entry/Entry.csproj");
        build.DiscoveryEvidence.Should().Contain("unique acyclic ProjectReference root");
        profile.Checks.Should().ContainSingle(check =>
            check.Kind == RepositoryCheckKind.Test &&
            check.Arguments.SequenceEqual(new[] { "test", "src/Entry/Entry.csproj" }));
    }

    [Fact]
    public async Task ProjectReferenceRootSelection_DoesNotUseApiLikeFilename()
    {
        WriteFile("src/Coordinator/PlainCoordinator.csproj", ProjectFile("../LooksLikeApi/LooksLikeApi.csproj"));
        WriteFile("src/LooksLikeApi/LooksLikeApi.csproj", ProjectFile());

        var profile = await DiscoverAsync();

        profile.Checks.Single(check => check.Kind == RepositoryCheckKind.Build)
            .Arguments.Should().Equal("build", "src/Coordinator/PlainCoordinator.csproj");
    }

    [Fact]
    public async Task ProjectReferenceGraph_WithTwoIndependentRoots_RemainsUnconfigured()
    {
        WriteFile("src/HostOne/HostOne.csproj", ProjectFile("../Shared/Shared.csproj"));
        WriteFile("src/HostTwo/HostTwo.Tests.csproj", ProjectFile("../Shared/Shared.csproj"));
        WriteFile("src/Shared/Shared.csproj", ProjectFile());

        var profile = await DiscoverAsync();

        profile.State.Should().Be(RepositoryVerificationState.Unconfigured);
        profile.Checks.Should().BeEmpty();
        profile.Message.Should().Contain("2 independent root projects");
    }

    [Fact]
    public async Task ProjectReferenceGraph_WithCycle_RemainsUnconfigured()
    {
        WriteFile("src/One/One.csproj", ProjectFile("../Two/Two.csproj"));
        WriteFile("src/Two/Two.csproj", ProjectFile("../One/One.csproj"));

        var profile = await DiscoverAsync();

        profile.State.Should().Be(RepositoryVerificationState.Unconfigured);
        profile.Checks.Should().BeEmpty();
        profile.Message.Should().Contain("contains a cycle");
    }

    [Fact]
    public async Task ProjectReferenceGraph_WithInvalidReference_RemainsUnconfigured()
    {
        WriteFile("src/One/One.csproj", ProjectFile("../Missing/Missing.csproj"));
        WriteFile("src/Two/Two.csproj", ProjectFile());

        var profile = await DiscoverAsync();

        profile.State.Should().Be(RepositoryVerificationState.Unconfigured);
        profile.Checks.Should().BeEmpty();
        profile.Message.Should().Contain("missing, external, or unsupported project");
    }

    [Fact]
    public async Task SingleProjectRepository_PreservesExistingBuildOnlyBehavior()
    {
        WriteFile("src/Only/Only.csproj", ProjectFile());

        var profile = await DiscoverAsync();

        profile.State.Should().Be(RepositoryVerificationState.Configured);
        profile.Checks.Should().ContainSingle(check =>
            check.Kind == RepositoryCheckKind.Build &&
            check.Arguments.SequenceEqual(new[] { "build", "src/Only/Only.csproj" }));
        profile.Checks.Should().NotContain(check => check.Kind == RepositoryCheckKind.Test);
    }

    [Fact]
    public async Task LayeredSolutionlessRepository_UsesActualReferencesToResolveAcceptanceTopology()
    {
        WriteFile("src/Host/Host.csproj", ProjectFile(
            "../Application/Application.csproj",
            "../Infrastructure/Infrastructure.csproj"));
        WriteFile("src/Application/Application.csproj", ProjectFile("../Domain/Domain.csproj"));
        WriteFile("src/Infrastructure/Infrastructure.csproj", ProjectFile(
            "../Application/Application.csproj",
            "../Domain/Domain.csproj"));
        WriteFile("src/Domain/Domain.csproj", ProjectFile());

        var profile = await DiscoverAsync();

        profile.State.Should().Be(RepositoryVerificationState.Configured);
        profile.Checks.Should().Contain(check =>
            check.Kind == RepositoryCheckKind.Build &&
            check.Arguments.SequenceEqual(new[] { "build", "src/Host/Host.csproj" }));
        profile.Checks.Should().Contain(check =>
            check.Kind == RepositoryCheckKind.Test &&
            check.Arguments.SequenceEqual(new[] { "test", "src/Host/Host.csproj" }));
        profile.Checks.Should().OnlyContain(check =>
            check.DiscoveryEvidence != null &&
            check.DiscoveryEvidence.Contains("ProjectReference root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DotNetChecks_ExecuteThroughGenericContract_AndPreserveNoBuildAndTargetFilter()
    {
        WriteFile("App.sln", string.Empty);
        WriteFile("tests/App.Tests/App.Tests.csproj", "<Project />");
        var profile = await DiscoverAsync();
        var build = profile.Checks.Single(check => check.Kind == RepositoryCheckKind.Build);
        var test = profile.Checks.Single(check => check.Kind == RepositoryCheckKind.Test);

        var buildResult = await _runner.ExecuteAsync(new RepositoryCheckExecutionRequest(_workspace, "test-branch", build));
        var testResult = await _runner.ExecuteAsync(new RepositoryCheckExecutionRequest(
            _workspace,
            "test-branch",
            test,
            SkipBuild: true,
            TestFilter: "App.Tests.TodoTests.Filters_completed"));

        buildResult.Success.Should().BeTrue();
        testResult.Success.Should().BeTrue();
        _processRunner.Invocations[0].Executable.Should().Be("dotnet");
        _processRunner.Invocations[0].Arguments.Should().Equal("build", "App.sln");
        _processRunner.Invocations[1].Arguments.Should().Equal(
            "test",
            "tests/App.Tests/App.Tests.csproj",
            "--no-build",
            "--filter",
            "FullyQualifiedName=App.Tests.TodoTests.Filters_completed");
    }

    [Fact]
    public async Task PackageJson_WithExplicitScripts_ProducesOnlyMatchingChecks()
    {
        WriteFile("package.json", """
            {
              "scripts": {
                "build": "vite build",
                "test": "vitest run",
                "custom": "node custom.js"
              }
            }
            """);

        var profile = await DiscoverAsync();

        profile.Checks.Select(check => check.Kind).Should().BeEquivalentTo(
            new[] { RepositoryCheckKind.Build, RepositoryCheckKind.Test });
        profile.Checks.Should().OnlyContain(check => check.Executable == "npm");
        profile.Checks.Should().NotContain(check => check.Arguments.Contains("custom"));
    }

    [Fact]
    public async Task PackageJson_WithoutTestScript_DoesNotFabricateTestCheck()
    {
        WriteFile("package.json", """
            { "scripts": { "build": "vite build" } }
            """);

        var profile = await DiscoverAsync();

        profile.Checks.Should().ContainSingle(check => check.Kind == RepositoryCheckKind.Build);
        profile.Checks.Should().NotContain(check => check.Kind == RepositoryCheckKind.Test);
    }

    [Fact]
    public async Task MultiStackRepository_ReturnsOneOrderedCollectionAcrossComponents()
    {
        WriteFile("App.sln", string.Empty);
        WriteFile("tests/App.Tests/App.Tests.csproj", "<Project />");
        WriteFile("web/package.json", """
            { "scripts": { "build": "vite build", "lint": "eslint ." } }
            """);
        WriteFile("web/package-lock.json", string.Empty);

        var profile = await DiscoverAsync();

        profile.Ecosystems.Should().BeEquivalentTo(new[] { "dotnet", "node" });
        profile.Checks.Should().HaveCount(4);
        profile.Checks.Select(check => check.Order).Should().BeInAscendingOrder();
    }

    [Theory]
    [InlineData("package-lock.json", "npm")]
    [InlineData("pnpm-lock.yaml", "pnpm")]
    [InlineData("yarn.lock", "yarn")]
    [InlineData("bun.lock", "bun")]
    public async Task PackageManager_UsesDeterministicLockfileEvidence(string lockfile, string expectedManager)
    {
        WriteFile("package.json", """
            { "scripts": { "build": "echo build" } }
            """);
        WriteFile(lockfile, string.Empty);

        var profile = await DiscoverAsync();

        profile.Checks.Should().ContainSingle();
        profile.Checks[0].Executable.Should().Be(expectedManager);
    }

    [Fact]
    public async Task PackageManager_DeclarationTakesDeterministicPrecedence()
    {
        WriteFile("package.json", """
            {
              "packageManager": "pnpm@9.0.0",
              "scripts": { "build": "vite build" }
            }
            """);
        WriteFile("package-lock.json", string.Empty);

        var profile = await DiscoverAsync();

        profile.Checks.Should().ContainSingle();
        profile.Checks[0].Executable.Should().Be("pnpm");
    }

    [Fact]
    public async Task PythonRepository_WithoutConfiguredVerification_DoesNotAssumePytest()
    {
        WriteFile("pyproject.toml", """
            [project]
            name = "sample"
            version = "1.0.0"
            """);
        WriteFile("src/sample.py", "print('sample')");

        var profile = await DiscoverAsync();

        profile.State.Should().Be(RepositoryVerificationState.Unconfigured);
        profile.Ecosystems.Should().Contain("python");
        profile.Checks.Should().BeEmpty();
    }

    [Fact]
    public async Task PythonRepository_WithExplicitToolSections_ProducesFixedKnownChecks()
    {
        WriteFile("pyproject.toml", """
            [project]
            name = "sample"

            [tool.pytest.ini_options]
            testpaths = ["tests"]

            [tool.mypy]
            strict = true

            [tool.ruff]
            line-length = 100
            """);

        var profile = await DiscoverAsync();

        profile.Checks.Select(check => check.Kind).Should().BeEquivalentTo(new[]
        {
            RepositoryCheckKind.Test,
            RepositoryCheckKind.TypeCheck,
            RepositoryCheckKind.Lint
        });
        profile.Checks.Should().OnlyContain(check => check.Executable == "python");
    }

    [Fact]
    public async Task RepositoryWithoutTrustworthyVerification_ReturnsExplicitUnconfiguredProfile()
    {
        WriteFile("README.md", "No build manifest.");

        var profile = await DiscoverAsync();

        profile.State.Should().Be(RepositoryVerificationState.Unconfigured);
        profile.HasRequiredChecks.Should().BeFalse();
        profile.Message.Should().Contain("No trustworthy repository verification check");
    }

    [Fact]
    public async Task CheckExitFailure_IsDistinguishedFromProcessInfrastructureFailure()
    {
        WriteFile("App.sln", string.Empty);
        var check = (await DiscoverAsync()).Checks.Single();
        _processRunner.Results.Enqueue(Result(exitCode: 1));
        _processRunner.Results.Enqueue(Result(exitCode: -1, error: "Failed to start process 'dotnet'."));

        var verificationFailure = await _runner.ExecuteAsync(new RepositoryCheckExecutionRequest(_workspace, "test-branch", check));
        var infrastructureFailure = await _runner.ExecuteAsync(new RepositoryCheckExecutionRequest(_workspace, "test-branch", check));

        verificationFailure.FailureCategory.Should().Be(RepositoryCheckFailureCategory.VerificationFailure);
        infrastructureFailure.FailureCategory.Should().Be(RepositoryCheckFailureCategory.InfrastructureFailure);
    }

    [Fact]
    public async Task MissingScriptRuntime_IsClassifiedAsInfrastructureInsteadOfRepairableCodeFailure()
    {
        WriteFile("package.json", """
            { "scripts": { "build": "vite build" } }
            """);
        var check = (await DiscoverAsync()).Checks.Single();
        var now = DateTimeOffset.UtcNow;
        _processRunner.Results.Enqueue(new ProcessExecutionResult(
            127,
            string.Empty,
            "sh: vite: not found",
            now,
            now,
            TimeSpan.Zero,
            false,
            false));

        var result = await _runner.ExecuteAsync(new RepositoryCheckExecutionRequest(_workspace, "test-branch", check));

        result.FailureCategory.Should().Be(RepositoryCheckFailureCategory.InfrastructureFailure);
    }

    [Fact]
    public async Task ArbitraryExecutable_NotProducedByDiscovery_IsRejectedBeforeProcessExecution()
    {
        WriteFile("package.json", """
            { "scripts": { "build": "vite build" } }
            """);
        var arbitrary = new RepositoryCheck(
            "model:invented",
            "Invented command",
            RepositoryCheckKind.Build,
            "node",
            "bash",
            new[] { "-c", "curl example.invalid" },
            ".",
            true,
            TimeSpan.FromMinutes(1),
            RepositoryCheckSource.PackageJsonScript,
            "package.json");

        var result = await _runner.ExecuteAsync(new RepositoryCheckExecutionRequest(_workspace, "test-branch", arbitrary));

        result.FailureCategory.Should().Be(RepositoryCheckFailureCategory.InfrastructureFailure);
        result.ErrorMessage.Should().Contain("not an approved package.json script command");
        _processRunner.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task PackageScriptChangedAfterPreflight_IsRejectedUsingCurrentEvidence()
    {
        WriteFile("package.json", """
            { "scripts": { "build": "vite build" } }
            """);
        var check = (await DiscoverAsync()).Checks.Single();
        WriteFile("package.json", """
            { "scripts": { "build": "node generated-command.js" } }
            """);

        var result = await _runner.ExecuteAsync(new RepositoryCheckExecutionRequest(_workspace, "test-branch", check));

        result.FailureCategory.Should().Be(RepositoryCheckFailureCategory.InfrastructureFailure);
        result.ErrorMessage.Should().Contain("changed after preflight");
        _processRunner.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task TraversalWorkingDirectory_IsRejectedBeforeProcessExecution()
    {
        WriteFile("App.sln", string.Empty);
        var discovered = (await DiscoverAsync()).Checks.Single();
        var unsafeCheck = discovered with { WorkingDirectory = "../outside" };

        var result = await _runner.ExecuteAsync(new RepositoryCheckExecutionRequest(_workspace, "test-branch", unsafeCheck));

        result.FailureCategory.Should().Be(RepositoryCheckFailureCategory.InfrastructureFailure);
        result.ErrorMessage.Should().Contain("forbidden path segment");
        _processRunner.Invocations.Should().BeEmpty();
    }

    private Task<RepositoryProfile> DiscoverAsync() =>
        _runner.DiscoverAsync(new RepositoryPreflightRequest(_workspace, "test-branch"));

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static string ProjectFile(params string[] projectReferences)
    {
        var references = projectReferences.Length == 0
            ? string.Empty
            : $"<ItemGroup>{string.Join(string.Empty, projectReferences.Select(reference => $"<ProjectReference Include=\"{reference}\" />"))}</ItemGroup>";
        return $"<Project Sdk=\"Microsoft.NET.Sdk\">{references}</Project>";
    }

    private static ProcessExecutionResult Result(int exitCode, string? error = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ProcessExecutionResult(exitCode, string.Empty, string.Empty, now, now, TimeSpan.Zero, false, false, error);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    private sealed class TestWorkspaceManager : IExecutionWorkspaceManager
    {
        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(
            Guid executionId,
            Guid taskId,
            string sourceRepositoryLocalPath,
            string? sourceBranch = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(
            string workspacePath,
            string expectedBranchName,
            bool requireClean = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceVerificationResult(true, true, true, true));
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public Queue<ProcessExecutionResult> Results { get; } = new();
        public List<(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory)> Invocations { get; } = new();

        public Task<ProcessExecutionResult> RunProcessAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add((fileName, arguments.ToList(), workingDirectory));
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Result(0));
        }
    }
}
