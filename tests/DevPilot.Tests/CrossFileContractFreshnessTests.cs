using System.Collections.Concurrent;
using System.Diagnostics;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public sealed class CrossFileContractFreshnessTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;

    public CrossFileContractFreshnessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotContractFreshness_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/contract-freshness";

        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);
        InitGitRepo(_originalRepoDir);
        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# Repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "Initial commit");
        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");
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
    public void DirectLocalImport_ToAnotherPlannedFile_CreatesPrerequisiteEdge()
    {
        var files = new[]
        {
            new ManifestFileEntry("models/entity.ts", FileEditAction.Modify),
            new ManifestFileEntry("repository/entityRepository.ts", FileEditAction.Modify)
        };
        var sources = new Dictionary<string, string>
        {
            ["models/entity.ts"] = "export interface CreateEntityInput { title: string; }",
            ["repository/entityRepository.ts"] = "import { CreateEntityInput } from '../models/entity';\nexport function create(input: CreateEntityInput) { return input; }"
        };

        var prerequisites = DeveloperAgent.CollectGenerationPrerequisites(files, sources);

        prerequisites.Should().ContainSingle(item =>
            item.ProducerPath == "models/entity.ts" &&
            item.ConsumerPath == "repository/entityRepository.ts" &&
            item.Reason == GenerationPrerequisiteReason.DirectLocalReference);
    }

    [Fact]
    public void UnrelatedPlannedFiles_RemainConcurrent()
    {
        var files = new[]
        {
            new ManifestFileEntry("models/alpha.ts", FileEditAction.Modify),
            new ManifestFileEntry("models/beta.ts", FileEditAction.Modify)
        };
        var sources = new Dictionary<string, string>
        {
            ["models/alpha.ts"] = "export interface Alpha { id: string; }",
            ["models/beta.ts"] = "export interface Beta { id: string; }"
        };

        DeveloperAgent.CollectGenerationPrerequisites(files, sources).Should().BeEmpty();
    }

    [Fact]
    public void ExplicitManifestDependency_StillCreatesEdge()
    {
        var files = new[]
        {
            new ManifestFileEntry("src/contracts/port.ts", FileEditAction.Create),
            new ManifestFileEntry(
                "src/app/handler.ts",
                FileEditAction.Create,
                "handler",
                new[] { "src/contracts/port.ts" })
        };

        var prerequisites = DeveloperAgent.CollectGenerationPrerequisites(files, new Dictionary<string, string>());

        prerequisites.Should().ContainSingle(item =>
            item.ProducerPath == "src/contracts/port.ts" &&
            item.ConsumerPath == "src/app/handler.ts" &&
            item.Reason == GenerationPrerequisiteReason.ManifestDependency);
    }

    [Fact]
    public void ExternalPackageImport_DoesNotCreatePlannedFileEdge()
    {
        var files = new[]
        {
            new ManifestFileEntry("models/entity.ts", FileEditAction.Modify),
            new ManifestFileEntry("repository/entityRepository.ts", FileEditAction.Modify)
        };
        var sources = new Dictionary<string, string>
        {
            ["models/entity.ts"] = "export interface CreateEntityInput { title: string; }",
            ["repository/entityRepository.ts"] = "import express from 'express';\nimport { Router } from 'express';\nexport const router = express();"
        };

        DeveloperAgent.CollectGenerationPrerequisites(files, sources)
            .Should()
            .NotContain(item => item.Reason == GenerationPrerequisiteReason.DirectLocalReference);
    }

    [Fact]
    public void ExistingHeuristic_RemainsFallbackWhenNoDirectReferenceOrManifestEdge()
    {
        var files = new[]
        {
            new ManifestFileEntry("src/Application/Dtos/TaskDto.cs", FileEditAction.Create),
            new ManifestFileEntry("src/Api/Controllers/TaskController.cs", FileEditAction.Create)
        };

        var prerequisites = DeveloperAgent.CollectGenerationPrerequisites(files);

        prerequisites.Should().ContainSingle(item =>
            item.ProducerPath == "src/Application/Dtos/TaskDto.cs" &&
            item.ConsumerPath == "src/Api/Controllers/TaskController.cs" &&
            item.Reason == GenerationPrerequisiteReason.ExistingHeuristic);
    }

    [Fact]
    public void SimilarFilenamesWithoutExactReference_DoNotInventAnalogOrNeighborEdges()
    {
        var files = new[]
        {
            new ManifestFileEntry("src/note.ts", FileEditAction.Modify),
            new ManifestFileEntry("src/notebook.ts", FileEditAction.Modify)
        };
        var sources = new Dictionary<string, string>
        {
            ["src/note.ts"] = "export const note = 1;",
            ["src/notebook.ts"] = "export const notebook = 1;"
        };

        var prerequisites = DeveloperAgent.CollectGenerationPrerequisites(files, sources);

        prerequisites.Should().BeEmpty();
        prerequisites.Should().NotContain(item => item.Reason == GenerationPrerequisiteReason.DirectLocalReference);
    }

    [Fact]
    public void PythonRelativeImport_ResolvesToPlannedFile()
    {
        var files = new[]
        {
            new ManifestFileEntry("pkg/models/entity.py", FileEditAction.Modify),
            new ManifestFileEntry("pkg/repo/entity_repository.py", FileEditAction.Modify)
        };
        var sources = new Dictionary<string, string>
        {
            ["pkg/models/entity.py"] = "class CreateEntityInput:\n    pass\n",
            ["pkg/repo/entity_repository.py"] = "from ..models.entity import CreateEntityInput\n"
        };

        DeveloperAgent.CollectGenerationPrerequisites(files, sources).Should().ContainSingle(item =>
            item.ProducerPath == "pkg/models/entity.py" &&
            item.ConsumerPath == "pkg/repo/entity_repository.py" &&
            item.Reason == GenerationPrerequisiteReason.DirectLocalReference);
    }

    [Fact]
    public void ConsumerPrompt_PrefersVirtualWorkspaceOverStaleContextSnapshot()
    {
        var consumer = new ManifestFileEntry("repository/entityRepository.ts", FileEditAction.Modify);
        var contextFiles = new Dictionary<string, string>
        {
            ["models/entity.ts"] = "export interface CreateEntityInput { title: string; /* STALE_CONTRACT_SNAPSHOT */ }",
            ["repository/entityRepository.ts"] = "import { CreateEntityInput } from '../models/entity';\nexport function create(input: CreateEntityInput) { return input.title; }"
        };
        var virtualWorkspace = new Dictionary<string, string>
        {
            ["models/entity.ts"] = "export interface CreateEntityInput { title: string; extraField: string; }"
        };
        var completedEdits = new Dictionary<string, FileEditSpec>
        {
            ["models/entity.ts"] = new(
                "models/entity.ts",
                FileEditAction.Modify,
                null,
                new[] { new SearchReplaceEdit("title: string;", "title: string; extraField: string;") })
        };

        var prompt = DeveloperAgent.BuildSingleFileUserPrompt(
            new DeveloperAgentRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Extend shared contract",
                "Add extraField to the shared input type",
                null,
                "Summary",
                "Plan",
                new[] { "models/entity.ts", "repository/entityRepository.ts" },
                _worktreeDir,
                _branchName),
            consumer,
            contextFiles,
            completedEdits,
            new List<DiscoveredProjectNode>(),
            lockedContracts: null,
            referencePattern: null,
            useFullFileReplacement: false,
            virtualWorkspace: virtualWorkspace);

        prompt.Should().Contain("extraField");
        prompt.Should().NotContain("STALE_CONTRACT_SNAPSHOT");
    }

    [Fact]
    public async Task ConsumerStartsAfterProducer_AndSeesLatestGeneratedContract()
    {
        WriteWorktreeFile("models/entity.ts", "export interface CreateEntityInput {\n  title: string;\n  /* STALE_CONTRACT_SNAPSHOT */\n}\n");
        WriteWorktreeFile(
            "repository/entityRepository.ts",
            "import { CreateEntityInput } from '../models/entity';\nexport function create(input: CreateEntityInput) {\n  return input.title;\n}\n");

        var startOrder = new ConcurrentQueue<string>();
        string? consumerPrompt = null;
        var producerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeAiProvider
        {
            CustomHandler = async (request, _) =>
            {
                var target = ExtractTargetFile(request.UserPrompt);
                startOrder.Enqueue(target);
                if (target.EndsWith("entityRepository.ts", StringComparison.OrdinalIgnoreCase))
                {
                    consumerPrompt = request.UserPrompt;
                    producerFinished.Task.IsCompleted.Should().BeTrue(
                        "consumer generation must start only after the producer finished publishing its contract");
                    return new AiResponse
                    {
                        IsSuccess = true,
                        Content = """{"filePath":"repository/entityRepository.ts","action":"Modify","searchReplaceEdits":[{"search":"return input.title;","replace":"return input.title + input.extraField;"}]}"""
                    };
                }

                await Task.Delay(75);
                producerFinished.TrySetResult();
                return new AiResponse
                {
                    IsSuccess = true,
                    Content = """{"filePath":"models/entity.ts","action":"Modify","searchReplaceEdits":[{"search":"  title: string;\n  /* STALE_CONTRACT_SNAPSHOT */","replace":"  title: string;\n  extraField: string;"}]}"""
                };
            }
        };

        var recorder = new RecordingActivityRecorder();
        var agent = new DeveloperAgent(
            provider,
            new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance),
            NullLogger<DeveloperAgent>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeveloperAgent:MaxConcurrentFileGenerations"] = "2"
            }).Build(),
            recorder);

        var result = await agent.GenerateAndApplyEditsAsync(new DeveloperAgentRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Extend shared contract",
            "Add extraField to the shared input and consume it",
            null,
            "Summary",
            "Plan",
            new[] { "models/entity.ts", "repository/entityRepository.ts" },
            _worktreeDir,
            _branchName,
            new[]
            {
                new ImpactedFileDetail("models/entity.ts", "Modify", "Update shared input"),
                new ImpactedFileDetail("repository/entityRepository.ts", "Modify", "Consume shared input")
            }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        startOrder.Should().Equal("models/entity.ts", "repository/entityRepository.ts");
        consumerPrompt.Should().NotBeNull();
        consumerPrompt.Should().Contain("extraField");
        consumerPrompt.Should().NotContain("STALE_CONTRACT_SNAPSHOT");
        recorder.Metadata.Any(metadata =>
            metadata != null &&
            metadata.EventKind == "GenerationPrerequisite" &&
            metadata.PrerequisiteReason == nameof(GenerationPrerequisiteReason.DirectLocalReference) &&
            metadata.PrerequisiteProducer == "models/entity.ts" &&
            metadata.PrerequisiteConsumer == "repository/entityRepository.ts").Should().BeTrue();
        File.ReadAllText(Path.Combine(_worktreeDir, "models", "entity.ts")).Should().Contain("extraField");
        File.ReadAllText(Path.Combine(_worktreeDir, "models", "entity.ts")).Should().NotContain("STALE_CONTRACT_SNAPSHOT");
    }

    private void WriteWorktreeFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_worktreeDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static string ExtractTargetFile(string prompt)
    {
        const string marker = "Target File: ";
        var start = prompt.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        var end = prompt.IndexOf('\n', start);
        return end < 0 ? prompt[start..].Trim() : prompt[start..end].Trim();
    }

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config", "user.name", "DevPilot Tests");
        RunGit(path, "config", "user.email", "tests@devpilot.local");
    }

    private static void RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
        }
    }

    private sealed class RecordingActivityRecorder : IExecutionActivityRecorder
    {
        public ConcurrentQueue<ExecutionActivityMetadata?> Metadata { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Metadata.Enqueue(metadata);
            message.Should().NotContain("export interface");
            return Task.CompletedTask;
        }
    }
}
