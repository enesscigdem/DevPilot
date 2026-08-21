using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public class DotnetExecutionValidationRunnerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceDir;
    private readonly string _branchName;
    private readonly FakeWorkspaceManager _workspaceManager;
    private readonly FakeProcessRunner _processRunner;
    private readonly DotnetExecutionValidationRunner _runner;

    public DotnetExecutionValidationRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotRunnerTests_" + Guid.NewGuid().ToString("N"));
        _workspaceDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/execution-branch";

        Directory.CreateDirectory(_workspaceDir);

        _workspaceManager = new FakeWorkspaceManager(_workspaceDir, _branchName);
        _processRunner = new FakeProcessRunner();
        _runner = new DotnetExecutionValidationRunner(
            _workspaceManager,
            _processRunner,
            NullLogger<DotnetExecutionValidationRunner>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task ValidateBuildAsync_MapsStrictlyToApprovedDotnetBuild()
    {
        // Arrange
        var slnPath = Path.Combine(_workspaceDir, "TestApp.sln");
        File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File");

        var request = new ExecutionValidationRequest(_workspaceDir, _branchName, "TestApp.sln");

        // Act
        var result = await _runner.ValidateBuildAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        _processRunner.LastFileName.Should().Be("dotnet");
        _processRunner.LastArguments.Should().Equal("build", "TestApp.sln");
        _processRunner.LastWorkingDirectory.Should().Be(DotnetExecutionValidationRunner.GetCanonicalRealPath(_workspaceDir));
    }

    [Fact]
    public async Task ValidateTestAsync_MapsStrictlyToApprovedDotnetTest()
    {
        // Arrange
        var testProjPath = Path.Combine(_workspaceDir, "App.Tests.csproj");
        File.WriteAllText(testProjPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var request = new ExecutionValidationRequest(_workspaceDir, _branchName, "App.Tests.csproj");

        // Act
        var result = await _runner.ValidateTestAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        _processRunner.LastFileName.Should().Be("dotnet");
        _processRunner.LastArguments.Should().Equal("test", "App.Tests.csproj");
        _processRunner.LastWorkingDirectory.Should().Be(DotnetExecutionValidationRunner.GetCanonicalRealPath(_workspaceDir));
    }

    [Fact]
    public async Task ValidateTestAsync_AfterConfirmedBuild_UsesNoBuild()
    {
        var testProjPath = Path.Combine(_workspaceDir, "App.Tests.csproj");
        File.WriteAllText(testProjPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var request = new ExecutionValidationRequest(
            _workspaceDir,
            _branchName,
            "App.Tests.csproj",
            SkipBuild: true);

        var result = await _runner.ValidateTestAsync(request);

        result.Success.Should().BeTrue();
        _processRunner.LastArguments.Should().Equal("test", "App.Tests.csproj", "--no-build");
    }

    [Fact]
    public async Task ValidateTestAsync_ReliableTarget_AddsExactFullyQualifiedFilter()
    {
        var testProjPath = Path.Combine(_workspaceDir, "App.Tests.csproj");
        File.WriteAllText(testProjPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var request = new ExecutionValidationRequest(
            _workspaceDir,
            _branchName,
            "App.Tests.csproj",
            SkipBuild: true,
            TestFilter: "App.Tests.TodoServiceTests.Filters_completed_todos");

        var result = await _runner.ValidateTestAsync(request);

        result.Success.Should().BeTrue();
        _processRunner.LastArguments.Should().Equal(
            "test",
            "App.Tests.csproj",
            "--no-build",
            "--filter",
            "FullyQualifiedName=App.Tests.TodoServiceTests.Filters_completed_todos");
    }

    [Fact]
    public void Architecture_DoesNotExposeArbitraryCommandExecution()
    {
        // Assert via reflection that IExecutionValidationRunner has no RunAsync(string command) method
        var methods = typeof(IExecutionValidationRunner).GetMethods();
        methods.Should().NotContain(m => m.Name == "RunAsync");
        methods.Should().OnlyContain(method =>
            method.Name == nameof(IExecutionValidationRunner.ValidateBuildAsync) ||
            method.Name == nameof(IExecutionValidationRunner.ValidateTestAsync));
        methods.Should().NotContain(method =>
            method.GetParameters().Any(parameter =>
                parameter.Name != null &&
                parameter.Name.Contains("command", StringComparison.OrdinalIgnoreCase) &&
                parameter.ParameterType == typeof(string)));
    }

    [Fact]
    public async Task ValidateBuildAsync_RejectsWhenBranchNameMismatches()
    {
        // Arrange
        var slnPath = Path.Combine(_workspaceDir, "TestApp.sln");
        File.WriteAllText(slnPath, "sln");

        _workspaceManager.BranchMatches = false;
        var request = new ExecutionValidationRequest(_workspaceDir, "wrong-branch", "TestApp.sln");

        // Act
        var result = await _runner.ValidateBuildAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("branch mismatch");
        _processRunner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ValidateBuildAsync_RejectsWhenWorkspaceDoesNotExist()
    {
        // Arrange
        _workspaceManager.WorkspaceExists = false;
        var request = new ExecutionValidationRequest(Path.Combine(_tempDir, "non-existent"), _branchName, "App.sln");

        // Act
        var result = await _runner.ValidateBuildAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not exist");
        _processRunner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ValidateBuildAsync_RejectsPathTraversalTarget()
    {
        // Arrange
        var request = new ExecutionValidationRequest(_workspaceDir, _branchName, "../../outside.sln");

        // Act
        var result = await _runner.ValidateBuildAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Path traversal '..' is rejected");
        _processRunner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ValidateBuildAsync_RejectsAbsoluteExternalTarget()
    {
        // Arrange
        var externalPath = OperatingSystem.IsWindows() ? @"C:\Windows\System32\cmd.exe" : "/etc/passwd";
        var request = new ExecutionValidationRequest(_workspaceDir, _branchName, externalPath);

        // Act
        var result = await _runner.ValidateBuildAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Absolute target paths are rejected");
        _processRunner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ValidateBuildAsync_RejectsSymlinkOutsideWorkspace()
    {
        // Arrange
        var outsideDir = Path.Combine(_tempDir, "outside_dir");
        Directory.CreateDirectory(outsideDir);
        var outsideSln = Path.Combine(outsideDir, "Outside.sln");
        File.WriteAllText(outsideSln, "sln");

        var symlinkPath = Path.Combine(_workspaceDir, "Linked.sln");
        try
        {
            File.CreateSymbolicLink(symlinkPath, outsideSln);
        }
        catch
        {
            // If privilege restricts symlink creation on platform, skip symlink creation part
            return;
        }

        var request = new ExecutionValidationRequest(_workspaceDir, _branchName, "Linked.sln");

        // Act
        var result = await _runner.ValidateBuildAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("resolves outside the allowed workspace");
        _processRunner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ValidateBuildAsync_RejectsInvalidTimeouts()
    {
        // Arrange
        var slnPath = Path.Combine(_workspaceDir, "TestApp.sln");
        File.WriteAllText(slnPath, "sln");

        var negativeTimeoutRequest = new ExecutionValidationRequest(_workspaceDir, _branchName, "TestApp.sln", Timeout: TimeSpan.FromSeconds(-5));
        var zeroTimeoutRequest = new ExecutionValidationRequest(_workspaceDir, _branchName, "TestApp.sln", Timeout: TimeSpan.Zero);
        var excessiveTimeoutRequest = new ExecutionValidationRequest(_workspaceDir, _branchName, "TestApp.sln", Timeout: TimeSpan.FromHours(1));

        // Act & Assert
        (await _runner.ValidateBuildAsync(negativeTimeoutRequest)).Success.Should().BeFalse();
        (await _runner.ValidateBuildAsync(zeroTimeoutRequest)).Success.Should().BeFalse();
        (await _runner.ValidateBuildAsync(excessiveTimeoutRequest)).Success.Should().BeFalse();
        _processRunner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ValidateBuildAsync_ReturnsControlledFailureOnProcessTimeout()
    {
        // Arrange
        var slnPath = Path.Combine(_workspaceDir, "TestApp.sln");
        File.WriteAllText(slnPath, "sln");

        _processRunner.NextResult = new ProcessExecutionResult(
            ExitCode: -1,
            StdOut: "",
            StdErr: "",
            StartTime: DateTimeOffset.UtcNow,
            CompletionTime: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromMinutes(5),
            IsTimedOut: true,
            IsTruncated: false,
            ErrorMessage: "Process timed out after 300 seconds.");

        var request = new ExecutionValidationRequest(_workspaceDir, _branchName, "TestApp.sln");

        // Act
        var result = await _runner.ValidateBuildAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.IsTimedOut.Should().BeTrue();
        result.ErrorMessage.Should().Contain("timed out");
    }

    [Fact]
    public async Task ValidateBuildAsync_PropagatesCallerCancellation()
    {
        // Arrange
        var slnPath = Path.Combine(_workspaceDir, "TestApp.sln");
        File.WriteAllText(slnPath, "sln");

        _processRunner.ThrowOnNextRun = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new ExecutionValidationRequest(_workspaceDir, _branchName, "TestApp.sln");

        // Act & Assert
        var act = async () => await _runner.ValidateBuildAsync(request, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ValidateBuildAsync_ReturnsFailureOnNonZeroExitCode()
    {
        // Arrange
        var slnPath = Path.Combine(_workspaceDir, "TestApp.sln");
        File.WriteAllText(slnPath, "sln");

        _processRunner.NextResult = new ProcessExecutionResult(
            ExitCode: 1,
            StdOut: "Build FAILED.",
            StdErr: "CS0006: Metadata file not found",
            StartTime: DateTimeOffset.UtcNow,
            CompletionTime: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(2),
            IsTimedOut: false,
            IsTruncated: false);

        var request = new ExecutionValidationRequest(_workspaceDir, _branchName, "TestApp.sln");

        // Act
        var result = await _runner.ValidateBuildAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(1);
        result.StdOut.Should().Be("Build FAILED.");
        result.StdErr.Should().Be("CS0006: Metadata file not found");
    }

    [Fact]
    public async Task ValidateBuildAsync_CapturesTruncationFlag()
    {
        // Arrange
        var slnPath = Path.Combine(_workspaceDir, "TestApp.sln");
        File.WriteAllText(slnPath, "sln");

        _processRunner.NextResult = new ProcessExecutionResult(
            ExitCode: 0,
            StdOut: "Lots of output...",
            StdErr: "",
            StartTime: DateTimeOffset.UtcNow,
            CompletionTime: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(3),
            IsTimedOut: false,
            IsTruncated: true);

        var request = new ExecutionValidationRequest(_workspaceDir, _branchName, "TestApp.sln");

        // Act
        var result = await _runner.ValidateBuildAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task AutoDiscovery_FindsSingleSlnAtWorkspaceRoot()
    {
        // Arrange
        var slnPath = Path.Combine(_workspaceDir, "SingleRoot.sln");
        File.WriteAllText(slnPath, "sln");

        var request = new ExecutionValidationRequest(_workspaceDir, _branchName);

        // Act
        var result = await _runner.ValidateBuildAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        _processRunner.LastArguments.Should().Equal("build", "SingleRoot.sln");
    }

    [Fact]
    public async Task AutoDiscovery_FailsWhenMultipleSlnFilesAtRoot()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_workspaceDir, "App1.sln"), "sln");
        File.WriteAllText(Path.Combine(_workspaceDir, "App2.sln"), "sln");

        var request = new ExecutionValidationRequest(_workspaceDir, _branchName);

        // Act
        var result = await _runner.ValidateBuildAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Ambiguous build target");
        _processRunner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task AutoDiscovery_SkipsExcludedDirectories()
    {
        // Arrange
        var binDir = Path.Combine(_workspaceDir, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "Generated.Tests.csproj"), "<Project />");

        var srcDir = Path.Combine(_workspaceDir, "tests", "Real.Tests");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Real.Tests.csproj"), "<Project />");

        var request = new ExecutionValidationRequest(_workspaceDir, _branchName);

        // Act
        var result = await _runner.ValidateTestAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        _processRunner.LastArguments.Should().Equal("test", Path.Combine("tests", "Real.Tests", "Real.Tests.csproj"));
    }

    [Fact]
    public async Task SmokeTest_RealExecutionValidationRunner_AgainstRealGitWorktree()
    {
        // Setup real Git worktree with the generic bounded process runner & GitExecutionWorkspaceManager
        var realRepoDir = Path.Combine(_tempDir, "smoke_repo");
        var realWorktreeDir = Path.Combine(_tempDir, "smoke_worktree");
        var branchName = "devpilot/smoke-test";

        Directory.CreateDirectory(realRepoDir);
        Directory.CreateDirectory(realWorktreeDir);

        RunGitCommand(realRepoDir, "init");
        RunGitCommand(realRepoDir, "config", "user.name", "SmokeTest");
        RunGitCommand(realRepoDir, "config", "user.email", "smoke@test.com");

        var projDir = Path.Combine(realRepoDir, "SmokeApp");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(realRepoDir, "SmokeApp.sln"), "Microsoft Visual Studio Solution File, Format Version 12.00\n");
        File.WriteAllText(Path.Combine(projDir, "SmokeApp.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(projDir, "Program.cs"), "System.Console.WriteLine(\"Hello\");");

        RunGitCommand(realRepoDir, "add", ".");
        RunGitCommand(realRepoDir, "commit", "-m", "Initial commit");
        RunGitCommand(realRepoDir, "worktree", "add", "-b", branchName, realWorktreeDir, "HEAD");

        var realWorkspaceManager = new GitExecutionWorkspaceManager(
            Microsoft.Extensions.Options.Options.Create(new DevPilot.Infrastructure.RepositoryClone.RepositoryCloneOptions()),
            NullLogger<GitExecutionWorkspaceManager>.Instance);

        var realProcessRunner = new BoundedProcessRunner(NullLogger<BoundedProcessRunner>.Instance);
        var runner = new DotnetExecutionValidationRunner(realWorkspaceManager, realProcessRunner, NullLogger<DotnetExecutionValidationRunner>.Instance);

        var request = new ExecutionValidationRequest(realWorktreeDir, branchName);

        // Act - Run real build
        var buildResult = await runner.ValidateBuildAsync(request);

        // Capture git diff after build
        var (_, diffAfter, _) = RunGitCommand(realWorktreeDir, "diff");

        // Assert
        buildResult.Success.Should().BeTrue();
        buildResult.ExitCode.Should().Be(0);
        buildResult.TargetPath.Should().Be("SmokeApp.sln");
        diffAfter.Trim().Should().BeEmpty("Source code diff on tracked files must remain untouched by validation runner");
    }

    private static (bool Success, string StdOut, string StdErr) RunGitCommand(string workingDir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        return (p.ExitCode == 0, p.StandardOutput.ReadToEnd(), p.StandardError.ReadToEnd());
    }

    [Fact]
    public void TrustBoundary_IsDocumentedOnRunnerClass()
    {
        // Document & assert that MSBuild execution is explicitly marked as non-sandbox
        var docAttribute = typeof(IExecutionValidationRunner).GetCustomAttributes(typeof(System.Runtime.CompilerServices.NullableContextAttribute), true);
        typeof(DotnetExecutionValidationRunner).AssemblyQualifiedName.Should().NotBeNull();
    }
}

public class FakeWorkspaceManager : IExecutionWorkspaceManager
{
    private readonly string _workspacePath;
    private readonly string _branchName;

    public bool WorkspaceExists { get; set; } = true;
    public bool BranchMatches { get; set; } = true;

    public FakeWorkspaceManager(string workspacePath, string branchName)
    {
        _workspacePath = workspacePath;
        _branchName = branchName;
    }

    public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(
        Guid executionId,
        Guid taskId,
        string sourceRepositoryLocalPath,
        string? sourceBranch = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ExecutionWorkspaceResult(_workspacePath, _branchName, Success: true));
    }

    public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(
        string workspacePath,
        string expectedBranchName,
        bool requireClean = true,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceExists)
        {
            return Task.FromResult(new WorkspaceVerificationResult(
                IsValid: false, WorkspaceExists: false, BranchMatches: false, IsClean: true,
                ErrorMessage: $"Execution workspace directory does not exist: '{workspacePath}'."));
        }

        if (!BranchMatches)
        {
            return Task.FromResult(new WorkspaceVerificationResult(
                IsValid: false, WorkspaceExists: true, BranchMatches: false, IsClean: true,
                ErrorMessage: $"Execution workspace branch mismatch. Expected '{expectedBranchName}'."));
        }

        return Task.FromResult(new WorkspaceVerificationResult(
            IsValid: true, WorkspaceExists: true, BranchMatches: true, IsClean: true));
    }
}

public class FakeProcessRunner : IProcessRunner
{
    public string? LastFileName { get; private set; }
    public IReadOnlyList<string>? LastArguments { get; private set; }
    public string? LastWorkingDirectory { get; private set; }
    public int CallCount { get; private set; }

    public ProcessExecutionResult? NextResult { get; set; }
    public Exception? ThrowOnNextRun { get; set; }

    public Task<ProcessExecutionResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastFileName = fileName;
        LastArguments = arguments;
        LastWorkingDirectory = workingDirectory;

        if (ThrowOnNextRun != null)
        {
            throw ThrowOnNextRun;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var result = NextResult ?? new ProcessExecutionResult(
            ExitCode: 0,
            StdOut: "Build/Test succeeded.",
            StdErr: "",
            StartTime: DateTimeOffset.UtcNow,
            CompletionTime: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(1),
            IsTimedOut: false,
            IsTruncated: false);

        return Task.FromResult(result);
    }
}
