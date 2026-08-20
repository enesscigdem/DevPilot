using System.Diagnostics;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public class ProviderResilienceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly WorktreeEditApplier _editApplier;
    private readonly DeveloperAgent _developerAgent;

    public ProviderResilienceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotResilienceTests_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/resilience-test-branch";

        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);

        InitGitRepo(_originalRepoDir);

        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# Original Repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "Initial commit");

        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");

        var inMemoryConfig = new Dictionary<string, string?>
        {
            ["DeveloperAgent:MaxGenerationCalls"] = "15"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemoryConfig)
            .Build();

        _fakeAiProvider = new FakeAiProvider { ProviderName = "Kimi" };
        _editApplier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        _developerAgent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            config);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_originalRepoDir))
            {
                RunGit(_originalRepoDir, "worktree", "prune");
            }
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_ProviderExhaustedTransientFailure_IsNotRetriedByDeveloperAgent()
    {
        // 4 planned files with layer scoring (File1: score 10, File2: score 20, File3: score 30, File4: score 60)
        var file1 = "src/Contracts/IOrderService.cs";
        var file2 = "src/Models/OrderDto.cs";
        var file3 = "src/Services/OrderService.cs";
        var file4 = "tests/OrderServiceTests.cs";

        // File 1: Success
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            Content = """
                {
                  "filePath": "src/Contracts/IOrderService.cs",
                  "action": "Create",
                  "newContent": "namespace Contracts;\npublic interface IOrderService { void ProcessOrder(); }"
                }
                """
        });

        // File 2: Success
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            Content = """
                {
                  "filePath": "src/Models/OrderDto.cs",
                  "action": "Create",
                  "newContent": "namespace Models;\npublic class OrderDto { public int Id { get; set; } }"
                }
                """
        });

        // File 3: Attempt 1 -> 503 Transient Service Unavailable
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            StatusCode = 503,
            AttemptCount = 4,
            FailureKind = AiFailureKind.TransientServiceUnavailable,
            ErrorMessage = "Kimi HTTP 503 service unavailable after 4 attempts."
        });

        // This response must remain unused: transport retry ownership belongs to the provider.
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            Content = """
                {
                  "filePath": "src/Services/OrderService.cs",
                  "action": "Create",
                  "newContent": "namespace Services;\nusing Contracts;\npublic class OrderService : IOrderService { public void ProcessOrder() {} }"
                }
                """
        });

        // File 4: Success
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            Content = """
                {
                  "filePath": "tests/OrderServiceTests.cs",
                  "action": "Create",
                  "newContent": "namespace Tests;\nusing Services;\npublic class OrderServiceTests { public void Test1() { var svc = new OrderService(); svc.ProcessOrder(); } }"
                }
                """
        });

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Add Order processing",
            TaskDescription: "Implement Order processing pipeline",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Order feature",
            ProposedPlan: "Add interface, dto, service, and tests",
            ImpactedFilePaths: new[] { file1, file2, file3, file4 },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("remained unavailable");

        // One logical call per reached file; DeveloperAgent does not retry the provider's exhausted failure.
        _fakeAiProvider.SendAsyncCallCount.Should().Be(3);

        File.Exists(Path.Combine(_worktreeDir, file1)).Should().BeFalse();
        File.Exists(Path.Combine(_worktreeDir, file2)).Should().BeFalse();
        File.Exists(Path.Combine(_worktreeDir, file3)).Should().BeFalse();
        File.Exists(Path.Combine(_worktreeDir, file4)).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_Persistent503AfterBoundedRetries_FailsClearly_NoPartialAtomicApply()
    {
        var file1 = "src/Contracts/IPaymentService.cs";
        var file2 = "src/Services/PaymentService.cs";

        // File 1: Success
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            Content = """
                {
                  "filePath": "src/Contracts/IPaymentService.cs",
                  "action": "Create",
                  "newContent": "namespace Contracts;\npublic interface IPaymentService { void Pay(); }"
                }
                """
        });

        // File 2: Initial call fails with 503
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            StatusCode = 503,
            AttemptCount = 4,
            FailureKind = AiFailureKind.TransientServiceUnavailable,
            ErrorMessage = "Kimi HTTP 503 service unavailable after 4 attempts."
        });

        // File 2: Recovery call also fails with 503
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            StatusCode = 503,
            AttemptCount = 4,
            FailureKind = AiFailureKind.TransientServiceUnavailable,
            ErrorMessage = "Kimi HTTP 503 service unavailable after 4 attempts."
        });

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Add Payment service",
            TaskDescription: "Implement Payment",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Payment feature",
            ProposedPlan: "Add interface and service",
            ImpactedFilePaths: new[] { file1, file2 },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("AI provider Kimi remained unavailable (HTTP 503) while generating 'src/Services/PaymentService.cs' after bounded retries.");

        // Exactly 2 logical calls: File1 and one provider-owned exhausted attempt for File2.
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);

        // Atomic apply MUST NOT have occurred: File1 was never applied to disk
        File.Exists(Path.Combine(_worktreeDir, file1)).Should().BeFalse();
        File.Exists(Path.Combine(_worktreeDir, file2)).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_Permanent400_DoesNotPerformFileRecoveryAttempt()
    {
        var file1 = "src/BadFile.cs";

        // File 1: Returns 400 Bad Request
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            StatusCode = 400,
            AttemptCount = 1,
            FailureKind = AiFailureKind.Permanent,
            ErrorMessage = "Kimi HTTP 400 after 1 attempt."
        });

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Bad file task",
            TaskDescription: "Bad file",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { file1 },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("AI provider Kimi failed with permanent error (HTTP 400) while generating 'src/BadFile.cs'.");

        // Exactly 1 AI call (no retry for permanent 400 error)
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);
    }


    [Fact]
    public async Task GenerateAndApplyEditsAsync_ExhaustedProviderFailure_DoesNotConsumeQueuedDeveloperRetry()
    {
        var file1 = "src/Models/CreateUserCommand.cs";
        var file2 = "src/Services/UserService.cs";

        // File 1: Declares CreateUserCommand with 2 parameters
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            Content = """
                {
                  "filePath": "src/Models/CreateUserCommand.cs",
                  "action": "Create",
                  "newContent": "namespace Models;\npublic record CreateUserCommand(string Name, int Age);"
                }
                """
        });

        // File 2: 503 on attempt 1
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            StatusCode = 503,
            AttemptCount = 4,
            FailureKind = AiFailureKind.TransientServiceUnavailable,
            ErrorMessage = "Kimi HTTP 503 service unavailable after 4 attempts."
        });

        // This queued response would have represented a duplicate DeveloperAgent retry and must remain unused.
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            Content = """
                {
                  "filePath": "src/Services/UserService.cs",
                  "action": "Create",
                  "newContent": "using Models;\nnamespace Services;\npublic class UserService { public void Run() { var cmd = new CreateUserCommand(); } }"
                }
                """
        });

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Add UserService",
            TaskDescription: "Implement UserService",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { file1, file2 },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("remained unavailable");
        _fakeAiProvider.StructuredResponsesToReturn.Should().ContainSingle("DeveloperAgent must not consume a second transport response");
        File.Exists(Path.Combine(_worktreeDir, file1)).Should().BeFalse();
        File.Exists(Path.Combine(_worktreeDir, file2)).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_ProviderOwnedRetryExhaustion_StopsAtomicGeneration()
    {
        var file1 = "src/Models/UserDto.cs";
        var file2 = "src/Models/AnotherUserDto.cs";

        // File 1: Declares UserDto
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            Content = """
                {
                  "filePath": "src/Models/UserDto.cs",
                  "action": "Create",
                  "newContent": "namespace Models;\npublic class UserDto { public string Name { get; set; } }"
                }
                """
        });

        // File 2: 503 on attempt 1
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            StatusCode = 503,
            AttemptCount = 4,
            FailureKind = AiFailureKind.TransientServiceUnavailable,
            ErrorMessage = "Kimi HTTP 503 service unavailable after 4 attempts."
        });

        // This queued response must remain unused because the provider owns transport retry.
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            Content = """
                {
                  "filePath": "src/Models/AnotherUserDto.cs",
                  "action": "Create",
                  "newContent": "namespace Models;\npublic class UserDto { public int Age { get; set; } }"
                }
                """
        });

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Add Models",
            TaskDescription: "Models",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { file1, file2 },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("remained unavailable");
        _fakeAiProvider.StructuredResponsesToReturn.Should().ContainSingle();
        File.Exists(Path.Combine(_worktreeDir, file1)).Should().BeFalse();
        File.Exists(Path.Combine(_worktreeDir, file2)).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Cancelled task",
            TaskDescription: "Cancelled",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "src/File1.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var act = async () => await _developerAgent.GenerateAndApplyEditsAsync(request, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_ProviderThrowsOperationCanceledException_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();

        _fakeAiProvider.CustomHandler = (req, ct) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Cancelled during provider call",
            TaskDescription: "Cancelled",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "src/File1.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var act = async () => await _developerAgent.GenerateAndApplyEditsAsync(request, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config", "user.name", "Test User");
        RunGit(path, "config", "user.email", "test@example.com");
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
