using System.Diagnostics;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.ProjectBrain.Models;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Domain.ProjectBrain.Entities;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace DevPilot.Application.ProjectBrain.Commands.IndexWorkspace;

public interface IIndexWorkspaceCommandHandler
{
    Task<IndexWorkspaceResult> HandleAsync(
        IndexWorkspaceCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class IndexWorkspaceCommandHandler : IIndexWorkspaceCommandHandler
{
    private readonly IRepositoryChunker _chunker;
    private readonly ICodeChunkRepository _chunkRepository;
    private readonly IIndexJobRepository _jobRepository;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ILogger<IndexWorkspaceCommandHandler> _logger;

    public IndexWorkspaceCommandHandler(
        IRepositoryChunker chunker,
        ICodeChunkRepository chunkRepository,
        IIndexJobRepository jobRepository,
        IEmbeddingProvider embeddingProvider,
        ILogger<IndexWorkspaceCommandHandler> logger)
    {
        _chunker = chunker;
        _chunkRepository = chunkRepository;
        _jobRepository = jobRepository;
        _embeddingProvider = embeddingProvider;
        _logger = logger;
    }

    public async Task<IndexWorkspaceResult> HandleAsync(
        IndexWorkspaceCommand command,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new IndexWorkspaceResult();
        var job = CreateJob(command);

        await _jobRepository.AddAsync(job, cancellationToken).ConfigureAwait(false);
        result.JobId = job.Id;

        try
        {
            if (string.IsNullOrWhiteSpace(command.WorkspacePath))
            {
                throw new ArgumentException("WorkspacePath is required.", nameof(command));
            }

            var workspacePath = Path.GetFullPath(command.WorkspacePath.Trim());
            if (!Directory.Exists(workspacePath))
            {
                throw new DirectoryNotFoundException($"Workspace path does not exist: {workspacePath}");
            }

            var workspaceName = command.WorkspaceName
                ?? Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            job.Status = IndexJobStatus.Running;
            job.WorkspacePath = workspacePath;
            job.WorkspaceName = workspaceName;
            job.UpdatedAt = DateTime.UtcNow;
            await _jobRepository.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

            var metadata = new ChunkMetadata
            {
                RepositoryWorkspaceId = command.RepositoryWorkspaceId,
                WorkspacePath = workspacePath,
                WorkspaceName = workspaceName,
                RoslynAnalysis = command.AnalysisResult,
            };

            var chunks = await _chunker.ChunkRepositoryAsync(
                metadata,
                command.AnalysisResult,
                cancellationToken).ConfigureAwait(false);

            if (chunks.Count == 0)
            {
                job.Status = IndexJobStatus.Completed;
                job.ProcessedFiles = 0;
                job.ProcessedChunks = 0;
                job.CompletedAt = DateTime.UtcNow;
                job.EmbeddingProviderStatus = "No chunks to embed";
                job.UpdatedAt = DateTime.UtcNow;
                await _jobRepository.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

                result.Success = true;
                result.EmbeddingProviderStatus = job.EmbeddingProviderStatus;
                result.Duration = stopwatch.Elapsed;
                return result;
            }

            var fileCount = chunks.Select(c => c.RelativePath).Distinct().Count();
            job.TotalFiles = fileCount;
            job.TotalChunks = chunks.Count;
            job.ProcessedFiles = 0;
            job.ProcessedChunks = 0;
            job.UpdatedAt = DateTime.UtcNow;
            await _jobRepository.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

            var relativePaths = chunks.Select(c => c.RelativePath).Distinct().ToList();
            var existingChunks = command.RepositoryWorkspaceId.HasValue
                ? await _chunkRepository.GetExistingChunksAsync(
                    command.RepositoryWorkspaceId.Value,
                    relativePaths,
                    cancellationToken).ConfigureAwait(false)
                : await _chunkRepository.GetExistingChunksAsync(
                    workspacePath,
                    relativePaths,
                    cancellationToken).ConfigureAwait(false);

            var (chunksToInsert, chunksToUpdate, skipped) = BuildDiff(chunks, existingChunks);

            var embeddingResult = await TryGenerateEmbeddingsAsync(
                chunksToInsert,
                chunksToUpdate,
                command.GenerateEmbeddings,
                cancellationToken).ConfigureAwait(false);

            result.EmbeddingProviderStatus = embeddingResult.Success
                ? $"Embeddings generated by {embeddingResult.ProviderName}"
                : embeddingResult.ErrorMessage;
            result.EmbeddingsGenerated = embeddingResult.Success;

            job.ChunksSkipped = skipped;
            job.ProcessedChunks = chunks.Count;
            job.ProcessedFiles = fileCount;
            job.ChunksEmbedded = embeddingResult.Success
                ? chunksToInsert.Count + chunksToUpdate.Count
                : 0;

            if (chunksToInsert.Count > 0)
            {
                await _chunkRepository.AddRangeAsync(chunksToInsert, cancellationToken).ConfigureAwait(false);
            }

            if (chunksToUpdate.Count > 0)
            {
                await _chunkRepository.UpdateRangeAsync(chunksToUpdate, cancellationToken).ConfigureAwait(false);
            }

            var currentPaths = chunks.Select(c => c.RelativePath).Distinct().ToList();
            var allExisting = command.RepositoryWorkspaceId.HasValue
                ? await _chunkRepository.GetAllByWorkspaceAsync(
                    command.RepositoryWorkspaceId.Value,
                    cancellationToken).ConfigureAwait(false)
                : await _chunkRepository.GetAllByWorkspaceAsync(
                    workspacePath,
                    cancellationToken).ConfigureAwait(false);
            var chunksToDelete = allExisting
                .Where(e => !currentPaths.Contains(e.RelativePath, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (chunksToDelete.Count > 0)
            {
                await _chunkRepository.DeleteRangeAsync(chunksToDelete, cancellationToken).ConfigureAwait(false);
            }

            job.Status = IndexJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            job.EmbeddingProviderStatus = result.EmbeddingProviderStatus;
            job.UpdatedAt = DateTime.UtcNow;
            await _jobRepository.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

            result.Success = true;
            result.FilesIndexed = fileCount;
            result.ChunksIndexed = chunksToInsert.Count;
            result.ChunksUpdated = chunksToUpdate.Count;
            result.ChunksSkipped = skipped;
            result.ChunksEmbedded = job.ChunksEmbedded;
            result.ChunksDeleted = chunksToDelete.Count;
            result.Duration = stopwatch.Elapsed;
        }

        catch (OperationCanceledException)
        {
            job.Status = IndexJobStatus.Cancelled;
            job.ErrorMessage = "Indexing was cancelled.";
            job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            await _jobRepository.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index workspace {WorkspacePath}", command.WorkspacePath);
            var sanitizedMessage = SanitizeErrorMessage(ex.Message, command.WorkspacePath);

            job.Status = IndexJobStatus.Failed;
            job.ErrorMessage = Truncate(sanitizedMessage, 1000);
            job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;

            try
            {
                await _jobRepository.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception persistEx)
            {
                _logger.LogError(persistEx, "Failed to persist IndexJob failure state for workspace {WorkspacePath}", command.WorkspacePath);
            }

            result.Success = false;
            result.ErrorMessage = sanitizedMessage;
            result.Duration = stopwatch.Elapsed;
        }

        return result;
    }

    private static IndexJob CreateJob(IndexWorkspaceCommand command)
    {
        var now = DateTime.UtcNow;
        return new IndexJob
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = command.RepositoryWorkspaceId,
            CommitSha = command.CommitSha,
            WorkspacePath = command.WorkspacePath ?? string.Empty,
            WorkspaceName = command.WorkspaceName ?? string.Empty,
            Status = IndexJobStatus.Pending,
            TotalFiles = 0,
            ProcessedFiles = 0,
            TotalChunks = 0,
            ProcessedChunks = 0,
            ChunksEmbedded = 0,
            ChunksSkipped = 0,
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static (List<CodeChunk> ToInsert, List<CodeChunk> ToUpdate, int Skipped) BuildDiff(
        IReadOnlyList<CodeChunk> discoveredChunks,
        IReadOnlyDictionary<string, CodeChunk> existingChunks)
    {
        var toInsert = new List<CodeChunk>();
        var toUpdate = new List<CodeChunk>();
        var skipped = 0;

        foreach (var chunk in discoveredChunks)
        {
            var key = chunk.GetLookupKey();
            if (existingChunks.TryGetValue(key, out var existing))
            {
                if (string.Equals(existing.ContentHash, chunk.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                }
                else
                {
                    chunk.Id = existing.Id;
                    chunk.CreatedAt = existing.CreatedAt;
                    chunk.UpdatedAt = DateTime.UtcNow;
                    chunk.IndexJobId = existing.IndexJobId;
                    toUpdate.Add(chunk);
                }
            }
            else
            {
                chunk.Id = Guid.NewGuid();
                chunk.CreatedAt = DateTime.UtcNow;
                chunk.UpdatedAt = DateTime.UtcNow;
                toInsert.Add(chunk);
            }
        }

        return (toInsert, toUpdate, skipped);
    }

    private async Task<EmbeddingResult> TryGenerateEmbeddingsAsync(
        List<CodeChunk> toInsert,
        List<CodeChunk> toUpdate,
        bool generateEmbeddings,
        CancellationToken cancellationToken)
    {
        var chunksToEmbed = toInsert
            .Concat(toUpdate)
            .Where(c => !string.IsNullOrWhiteSpace(c.Content))
            .ToList();

        if (chunksToEmbed.Count == 0 || !generateEmbeddings)
        {
            return EmbeddingResult.NotConfigured(_embeddingProvider.ProviderName);
        }

        var texts = chunksToEmbed.Select(c => c.Content).ToList();
        var embeddingResult = await _embeddingProvider
            .GenerateAsync(texts, cancellationToken)
            .ConfigureAwait(false);

        if (!embeddingResult.Success)
        {
            return embeddingResult;
        }

        if (embeddingResult.Embeddings.Count != chunksToEmbed.Count)
        {
            return new EmbeddingResult
            {
                Success = false,
                ProviderName = embeddingResult.ProviderName,
                ErrorMessage = $"Embedding provider returned {embeddingResult.Embeddings.Count} embeddings for {chunksToEmbed.Count} chunks.",
            };
        }

        for (int i = 0; i < chunksToEmbed.Count; i++)
        {
            var vector = embeddingResult.Embeddings[i];
            if (vector is null || vector.Length == 0)
            {
                return new EmbeddingResult
                {
                    Success = false,
                    ProviderName = embeddingResult.ProviderName,
                    ErrorMessage = "Embedding provider returned an empty vector.",
                };
            }

            chunksToEmbed[i].Embedding = new Vector(vector);
        }

        return embeddingResult;
    }

    private static string SanitizeErrorMessage(string? rawMessage, string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return "An unexpected error occurred during repository indexing.";
        }

        var message = rawMessage;

        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            var normalizedPath = Path.GetFullPath(workspacePath.Trim());
            message = message.Replace(normalizedPath, "[workspace]", StringComparison.OrdinalIgnoreCase);
            message = message.Replace(normalizedPath.Replace('\\', '/'), "[workspace]", StringComparison.OrdinalIgnoreCase);
        }

        message = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"(Password|pwd|token|secret|key|bearer)\s*[:=]\s*[^\s;,]+",
            "$1=***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return message.Length > 1000 ? message[..1000] : message;
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
