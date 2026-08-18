using DevPilot.Application.ProjectBrain.Commands.IndexWorkspace;
using DevPilot.Application.ProjectBrain.Models;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Domain.ProjectBrain.Entities;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.ProjectBrain;
using DevPilot.Infrastructure.ProjectBrain.EmbeddingProviders;
using DevPilot.Infrastructure.ProjectBrain.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.ProjectBrain;

public sealed class IndexWorkspaceCommandHandlerTests : IDisposable
{
    private readonly string _tempWorkspace;
    private readonly DevPilotDbContext _dbContext;
    private readonly ICodeChunkRepository _chunkRepository;
    private readonly IIndexJobRepository _jobRepository;
    private readonly IRepositoryChunker _chunker;
    private readonly IndexWorkspaceCommandHandler _handler;

    public IndexWorkspaceCommandHandlerTests()
    {
        _tempWorkspace = Path.Combine(Path.GetTempPath(), "IndexWorkspaceTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspace);

        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase("IndexWorkspaceTestDb_" + Guid.NewGuid().ToString("N"))
            .Options;

        _dbContext = new DevPilotDbContext(options);
        _chunkRepository = new EfCodeChunkRepository(_dbContext);
        _jobRepository = new EfIndexJobRepository(_dbContext);
        _chunker = new RepositoryChunker(NullLogger<RepositoryChunker>.Instance);

        _handler = new IndexWorkspaceCommandHandler(
            _chunker,
            _chunkRepository,
            _jobRepository,
            new NullEmbeddingProvider(),
            NullLogger<IndexWorkspaceCommandHandler>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspace))
            {
                Directory.Delete(_tempWorkspace, recursive: true);
            }
        }
        catch
        {
            // ignore cleanup errors in tests
        }
    }

    [Fact]
    public async Task HandleAsync_IndexesFilesAndScopesToWorkspaceId()
    {
        var workspaceId = Guid.NewGuid();
        var srcDir = Path.Combine(_tempWorkspace, "src");
        Directory.CreateDirectory(srcDir);

        File.WriteAllText(
            Path.Combine(srcDir, "KeyValueService.cs"),
            "public class KeyValueService { public string GetKey() => \"val\"; }");

        var command = new IndexWorkspaceCommand(
            WorkspacePath: _tempWorkspace,
            WorkspaceName: "testowner/testrepo",
            RepositoryWorkspaceId: workspaceId,
            CommitSha: "commit_abc");

        var result = await _handler.HandleAsync(command);

        result.Success.Should().BeTrue();
        result.FilesIndexed.Should().Be(1);
        result.ChunksIndexed.Should().Be(1);

        var storedChunks = await _chunkRepository.GetAllByWorkspaceAsync(workspaceId);
        storedChunks.Should().HaveCount(1);
        storedChunks[0].RepositoryWorkspaceId.Should().Be(workspaceId);
        storedChunks[0].RelativePath.Should().Be("src/KeyValueService.cs");
        storedChunks[0].RelativePath.Should().NotContain("\\");
    }

    [Fact]
    public async Task HandleAsync_Reindexing_SkipsUnchangedAndDeletesRemovedFiles()
    {
        var workspaceId = Guid.NewGuid();
        var file1 = Path.Combine(_tempWorkspace, "File1.cs");
        var file2 = Path.Combine(_tempWorkspace, "File2.cs");

        File.WriteAllText(file1, "public class FileOne {}");
        File.WriteAllText(file2, "public class FileTwo {}");

        var command1 = new IndexWorkspaceCommand(
            WorkspacePath: _tempWorkspace,
            WorkspaceName: "testowner/testrepo",
            RepositoryWorkspaceId: workspaceId,
            CommitSha: "commit_1");

        var result1 = await _handler.HandleAsync(command1);
        result1.ChunksIndexed.Should().Be(2);

        // Modify file1 slightly, delete file2, add file3
        File.WriteAllText(file1, "public class FileOneModified {}");
        File.Delete(file2);
        var file3 = Path.Combine(_tempWorkspace, "File3.cs");
        File.WriteAllText(file3, "public class FileThree {}");

        var command2 = new IndexWorkspaceCommand(
            WorkspacePath: _tempWorkspace,
            WorkspaceName: "testowner/testrepo",
            RepositoryWorkspaceId: workspaceId,
            CommitSha: "commit_2");

        var result2 = await _handler.HandleAsync(command2);

        result2.Success.Should().BeTrue();
        result2.ChunksUpdated.Should().Be(1); // File1
        result2.ChunksIndexed.Should().Be(1); // File3
        result2.ChunksDeleted.Should().Be(1); // File2 was deleted

        var storedChunks = await _chunkRepository.GetAllByWorkspaceAsync(workspaceId);
        storedChunks.Should().HaveCount(2);
        storedChunks.Select(c => c.RelativePath).Should().BeEquivalentTo(new[] { "File1.cs", "File3.cs" });
    }

    [Fact]
    public async Task HandleAsync_ExcludesSensitiveFiles_AndExcludedDirectories()
    {
        var workspaceId = Guid.NewGuid();

        // Valid source file with "Key" in name
        File.WriteAllText(
            Path.Combine(_tempWorkspace, "KeyboardHandler.cs"),
            "public class KeyboardHandler {}");

        // Excluded sensitive files
        File.WriteAllText(Path.Combine(_tempWorkspace, ".env"), "SECRET_KEY=12345");
        File.WriteAllText(Path.Combine(_tempWorkspace, "secrets.json"), "{ \"apiKey\": \"abc\" }");
        File.WriteAllText(Path.Combine(_tempWorkspace, "server.key"), "PRIVATE KEY");
        File.WriteAllText(Path.Combine(_tempWorkspace, "appsettings.Development.json"), "{ \"Conn\": \"secret\" }");

        // Excluded directories
        var binDir = Path.Combine(_tempWorkspace, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "Ignored.cs"), "public class Ignored {}");

        var command = new IndexWorkspaceCommand(
            WorkspacePath: _tempWorkspace,
            WorkspaceName: "testowner/testrepo",
            RepositoryWorkspaceId: workspaceId);

        var result = await _handler.HandleAsync(command);

        result.Success.Should().BeTrue();
        result.FilesIndexed.Should().Be(1); // Only KeyboardHandler.cs

        var storedChunks = await _chunkRepository.GetAllByWorkspaceAsync(workspaceId);
        storedChunks.Should().HaveCount(1);
        storedChunks[0].RelativePath.Should().Be("KeyboardHandler.cs");
    }

    [Fact]
    public async Task HandleAsync_WhenExceptionHasMessageLongerThan200Chars_DoesNotMaskOriginalError_AndSavesFailedJob()
    {
        var workspaceId = Guid.NewGuid();
        var longExceptionMessage = "Critical indexing failure occurred due to internal parsing error in AST: " +
                                  new string('X', 400) +
                                  " with additional stack diagnostics details.";

        var mockChunker = new FailingChunker(new InvalidOperationException(longExceptionMessage));
        var handler = new IndexWorkspaceCommandHandler(
            mockChunker,
            _chunkRepository,
            _jobRepository,
            new NullEmbeddingProvider(),
            NullLogger<IndexWorkspaceCommandHandler>.Instance);

        var srcDir = Path.Combine(_tempWorkspace, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Test.cs"), "public class Test {}");

        var command = new IndexWorkspaceCommand(
            WorkspacePath: _tempWorkspace,
            WorkspaceName: "testowner/testrepo",
            RepositoryWorkspaceId: workspaceId);

        var result = await handler.HandleAsync(command);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Critical indexing failure occurred");
        result.JobId.Should().NotBeEmpty();

        var job = await _jobRepository.GetByIdAsync(result.JobId);
        job.Should().NotBeNull();
        job!.Status.Should().Be(IndexJobStatus.Failed);
        job.ErrorMessage.Should().NotBeNull();
        job.ErrorMessage!.Length.Should().BeLessThanOrEqualTo(1000);
        job.ErrorMessage.Should().Contain("Critical indexing failure occurred");
    }

    [Fact]
    public async Task HandleAsync_WhenExceptionOccurs_SanitizesLocalPathAndSecretsFromErrorMessage()
    {
        var workspaceId = Guid.NewGuid();
        var pathWithSecret = $"Failed to access file at {_tempWorkspace}\\secret.cs with Password=SuperSecretPassword123! in token=abc123secret";

        var mockChunker = new FailingChunker(new IOException(pathWithSecret));
        var handler = new IndexWorkspaceCommandHandler(
            mockChunker,
            _chunkRepository,
            _jobRepository,
            new NullEmbeddingProvider(),
            NullLogger<IndexWorkspaceCommandHandler>.Instance);

        var srcDir = Path.Combine(_tempWorkspace, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Test.cs"), "public class Test {}");

        var command = new IndexWorkspaceCommand(
            WorkspacePath: _tempWorkspace,
            WorkspaceName: "testowner/testrepo",
            RepositoryWorkspaceId: workspaceId);

        var result = await handler.HandleAsync(command);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotContain(_tempWorkspace);
        result.ErrorMessage.Should().Contain("[workspace]");
        result.ErrorMessage.Should().NotContain("SuperSecretPassword123!");
        result.ErrorMessage.Should().Contain("Password=***");

        var job = await _jobRepository.GetByIdAsync(result.JobId);
        job.Should().NotBeNull();
        job!.ErrorMessage.Should().NotContain(_tempWorkspace);
        job.ErrorMessage.Should().NotContain("SuperSecretPassword123!");
    }

    [Fact]
    public async Task HandleAsync_WhenChunkingWithManyMethods_SafelyBoundsMethodNamesUnder200Chars()
    {
        var workspaceId = Guid.NewGuid();
        var srcDir = Path.Combine(_tempWorkspace, "src");
        Directory.CreateDirectory(srcDir);

        // Generate a class with 30 methods whose names combined easily exceed 300 characters
        var classContent = "public class MegaService {\n" +
            string.Join("\n", Enumerable.Range(1, 30).Select(i => $"    public void ExecuteComplexBusinessOperationNumber{i:D3}() {{ }}")) +
            "\n}";

        File.WriteAllText(Path.Combine(srcDir, "MegaService.cs"), classContent);

        var analysisResult = new DevPilot.Application.CodeAnalysis.RepositoryAnalysisResult
        {
            Solutions = new List<DevPilot.Application.CodeAnalysis.SolutionAnalysisResult>
            {
                new()
                {
                    Name = "TestSol",
                    Path = Path.Combine(_tempWorkspace, "TestSol.sln"),
                    Projects = new List<DevPilot.Application.CodeAnalysis.ProjectAnalysisResult>
                    {
                        new()
                        {
                            Name = "TestProject",
                            Path = Path.Combine(srcDir, "TestProject.csproj"),
                            Classes = new List<DevPilot.Application.CodeAnalysis.TypeAnalysisResult>
                            {
                                new()
                                {
                                    Name = "MegaService",
                                    SourcePath = Path.Combine(srcDir, "MegaService.cs"),
                                    Methods = Enumerable.Range(1, 30)
                                        .Select(i => new DevPilot.Application.CodeAnalysis.MethodAnalysisResult
                                        {
                                            Name = $"ExecuteComplexBusinessOperationNumber{i:D3}"
                                        }).ToList()
                                }
                            }
                        }
                    }
                }
            }
        };

        var command = new IndexWorkspaceCommand(
            WorkspacePath: _tempWorkspace,
            WorkspaceName: "testowner/testrepo",
            AnalysisResult: analysisResult,
            RepositoryWorkspaceId: workspaceId);

        var result = await _handler.HandleAsync(command);

        result.Success.Should().BeTrue();
        var chunks = await _chunkRepository.GetAllByWorkspaceAsync(workspaceId);
        chunks.Should().NotBeEmpty();
        foreach (var chunk in chunks)
        {
            if (chunk.MethodName != null)
            {
                chunk.MethodName.Length.Should().BeLessThanOrEqualTo(200);
            }
            if (chunk.TypeName != null)
            {
                chunk.TypeName.Length.Should().BeLessThanOrEqualTo(200);
            }
            if (chunk.SymbolName != null)
            {
                chunk.SymbolName.Length.Should().BeLessThanOrEqualTo(200);
            }
        }
    }

    [Fact]
    public async Task HandleAsync_WhenChunkRepositoryFails_DoesNotThrowAndSavesFailedJob()
    {
        var workspaceId = Guid.NewGuid();
        var srcDir = Path.Combine(_tempWorkspace, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Sample.cs"), "public class Sample {}");

        var failingChunkRepo = new FailingCodeChunkRepository(new DbUpdateException("Simulated database failure during chunk insertion"));
        var handler = new IndexWorkspaceCommandHandler(
            _chunker,
            failingChunkRepo,
            _jobRepository,
            new NullEmbeddingProvider(),
            NullLogger<IndexWorkspaceCommandHandler>.Instance);

        var command = new IndexWorkspaceCommand(
            WorkspacePath: _tempWorkspace,
            WorkspaceName: "testowner/testrepo",
            RepositoryWorkspaceId: workspaceId);

        var result = await handler.HandleAsync(command);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Simulated database failure");
        result.JobId.Should().NotBeEmpty();

        var job = await _jobRepository.GetByIdAsync(result.JobId);
        job.Should().NotBeNull();
        job!.Status.Should().Be(IndexJobStatus.Failed);
        job.ErrorMessage.Should().Contain("Simulated database failure");
    }

    private sealed class FailingCodeChunkRepository : ICodeChunkRepository
    {
        private readonly Exception _exception;

        public FailingCodeChunkRepository(Exception exception)
        {
            _exception = exception;
        }

        public Task<IReadOnlyDictionary<string, CodeChunk>> GetExistingChunksAsync(Guid repositoryWorkspaceId, IEnumerable<string> relativePaths, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, CodeChunk>>(new Dictionary<string, CodeChunk>());

        public Task<IReadOnlyDictionary<string, CodeChunk>> GetExistingChunksAsync(string workspacePath, IEnumerable<string> relativePaths, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, CodeChunk>>(new Dictionary<string, CodeChunk>());

        public Task<IReadOnlyList<CodeChunk>> GetAllByWorkspaceAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CodeChunk>>(Array.Empty<CodeChunk>());

        public Task<IReadOnlyList<CodeChunk>> GetAllByWorkspaceAsync(string workspacePath, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CodeChunk>>(Array.Empty<CodeChunk>());

        public Task AddRangeAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default)
            => throw _exception;

        public Task UpdateRangeAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default)
            => throw _exception;

        public Task DeleteRangeAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default)
            => throw _exception;

        public Task<int> CountByWorkspaceAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<int> CountByWorkspaceAsync(string workspacePath, CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private sealed class FailingChunker : IRepositoryChunker
    {
        private readonly Exception _exception;

        public FailingChunker(Exception exception)
        {
            _exception = exception;
        }

        public Task<IReadOnlyList<CodeChunk>> ChunkRepositoryAsync(
            ChunkMetadata metadata,
            DevPilot.Application.CodeAnalysis.RepositoryAnalysisResult? analysisResult = null,
            CancellationToken cancellationToken = default)
        {
            throw _exception;
        }
    }
}

