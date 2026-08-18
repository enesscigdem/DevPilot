using DevPilot.Application.ProjectBrain.Models;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Domain.ProjectBrain.Entities;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.ProjectBrain.Queries.GetBrainStatus;

public sealed class GetBrainStatusQueryHandler : IGetBrainStatusQueryHandler
{
    private readonly IRepositoryWorkspaceQuery _workspaceQuery;
    private readonly ICodeChunkRepository _chunkRepository;
    private readonly IIndexJobRepository _jobRepository;
    private readonly ILogger<GetBrainStatusQueryHandler> _logger;

    public GetBrainStatusQueryHandler(
        IRepositoryWorkspaceQuery workspaceQuery,
        ICodeChunkRepository chunkRepository,
        IIndexJobRepository jobRepository,
        ILogger<GetBrainStatusQueryHandler> logger)
    {
        _workspaceQuery = workspaceQuery;
        _chunkRepository = chunkRepository;
        _jobRepository = jobRepository;
        _logger = logger;
    }

    public async Task<GetBrainStatusResult> HandleAsync(
        GetBrainStatusQuery query,
        CancellationToken cancellationToken = default)
    {
        var workspace = await _workspaceQuery
            .GetByIdAsync(query.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        if (workspace is null)
        {
            return new GetBrainStatusResult
            {
                NotFound = true,
                ErrorMessage = $"Repository workspace {query.WorkspaceId} was not found.",
            };
        }

        var chunks = await _chunkRepository
            .GetAllByWorkspaceAsync(query.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        var latestJob = await _jobRepository
            .GetLatestByWorkspaceAsync(query.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        var totalFiles = chunks.Select(c => c.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var totalChunks = chunks.Count;
        var totalTypes = chunks.Where(c => !string.IsNullOrWhiteSpace(c.TypeName))
            .Select(c => c.TypeName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var totalSymbols = chunks
            .Where(c => !string.IsNullOrWhiteSpace(c.DeclaredSymbols))
            .SelectMany(c => c.DeclaredSymbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (totalSymbols == 0)
        {
            totalSymbols = chunks
                .Where(c => !string.IsNullOrWhiteSpace(c.SymbolName))
                .Select(c => c.SymbolName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        // Determine state honestly
        string state;
        if (workspace.Status != RepositoryWorkspaceStatus.Completed)
        {
            state = "unindexed";
        }
        else if (latestJob is null || totalChunks == 0)
        {
            state = "unindexed";
        }
        else if (latestJob.Status == IndexJobStatus.Running || latestJob.Status == IndexJobStatus.Pending)
        {
            state = "indexing";
        }
        else if (latestJob.Status == IndexJobStatus.Failed)
        {
            state = "failed";
        }
        else if (!string.IsNullOrEmpty(workspace.CommitSha) &&
                 !string.IsNullOrEmpty(latestJob.CommitSha) &&
                 !string.Equals(workspace.CommitSha, latestJob.CommitSha, StringComparison.OrdinalIgnoreCase))
        {
            state = "stale";
        }
        else
        {
            state = "ready";
        }

        // Honest pipeline steps that genuinely happened
        var embeddingsGenerated = latestJob != null && latestJob.ChunksEmbedded > 0;
        var steps = new List<BrainIndexStepDto>
        {
            new() { Label = "Discover & filter repository files", Done = totalFiles > 0 },
            new() { Label = "Roslyn symbol analysis", Done = totalSymbols > 0 || totalChunks > 0 },
            new() { Label = "Chunk code & calculate hashes", Done = totalChunks > 0 },
            new() { Label = "Persist chunks to PostgreSQL", Done = totalChunks > 0 },
            new() { Label = "Index embeddings", Done = embeddingsGenerated },
        };

        // Honest source groups derived from real indexed chunks
        var sourceGroups = chunks
            .GroupBy(c => string.IsNullOrWhiteSpace(c.ProjectName) ? "Root" : c.ProjectName)
            .Select(g =>
            {
                var groupSymbols = g
                    .Where(c => !string.IsNullOrWhiteSpace(c.DeclaredSymbols))
                    .SelectMany(c => c.DeclaredSymbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                if (groupSymbols == 0)
                {
                    groupSymbols = g
                        .Where(c => !string.IsNullOrWhiteSpace(c.SymbolName))
                        .Select(c => c.SymbolName!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();
                }

                return new BrainSourceGroupDto
                {
                    Project = g.Key,
                    Layer = DetectLayer(g.Key),
                    Files = g.Select(c => c.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    Symbols = groupSymbols,
                    Indexed = true,
                };
            })
            .OrderBy(g => g.Project)
            .ToList();

        // Dynamic suggested questions based on real projects and symbols
        var suggestedQuestions = GenerateSuggestedQuestions(sourceGroups, chunks);

        var lastIndexedAt = latestJob?.CompletedAt ?? latestJob?.StartedAt;

        var statusDto = new BrainStatusDto
        {
            WorkspaceId = workspace.Id,
            State = state,
            TotalFiles = totalFiles,
            TotalTypes = totalTypes,
            TotalSymbols = totalSymbols,
            TotalChunks = totalChunks,
            LastIndexedAt = lastIndexedAt,
            LastIndexedRelative = FormatRelativeTime(lastIndexedAt),
            Engine = "Roslyn workspace analysis",
            Steps = steps,
            SourceGroups = sourceGroups,
            SuggestedQuestions = suggestedQuestions,
        };

        return new GetBrainStatusResult
        {
            Success = true,
            Status = statusDto,
        };
    }

    private static string DetectLayer(string projectName)
    {
        if (projectName.Contains("Api", StringComparison.OrdinalIgnoreCase) ||
            projectName.Contains("Web", StringComparison.OrdinalIgnoreCase) ||
            projectName.Contains("Client", StringComparison.OrdinalIgnoreCase))
        {
            return "Web";
        }

        if (projectName.Contains("Application", StringComparison.OrdinalIgnoreCase) ||
            projectName.Contains("App", StringComparison.OrdinalIgnoreCase))
        {
            return "Application";
        }

        if (projectName.Contains("Domain", StringComparison.OrdinalIgnoreCase) ||
            projectName.Contains("Core", StringComparison.OrdinalIgnoreCase))
        {
            return "Domain";
        }

        if (projectName.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase) ||
            projectName.Contains("Data", StringComparison.OrdinalIgnoreCase))
        {
            return "Infrastructure";
        }

        if (projectName.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
            projectName.Contains("Tests", StringComparison.OrdinalIgnoreCase))
        {
            return "Tests";
        }

        return "Unknown";
    }

    private static List<string> GenerateSuggestedQuestions(
        List<BrainSourceGroupDto> sourceGroups,
        IReadOnlyList<CodeChunk> chunks)
    {
        var questions = new List<string>();

        var hasAuth = chunks.Any(c =>
            c.RelativePath.Contains("Auth", StringComparison.OrdinalIgnoreCase) ||
            (c.SymbolName != null && c.SymbolName.Contains("Auth", StringComparison.OrdinalIgnoreCase)));

        var hasDatabase = chunks.Any(c =>
            c.RelativePath.Contains("DbContext", StringComparison.OrdinalIgnoreCase) ||
            c.RelativePath.Contains("Repository", StringComparison.OrdinalIgnoreCase));

        var hasApi = sourceGroups.Any(g => g.Layer == "Web");

        if (hasAuth)
        {
            questions.Add("How does authentication work?");
        }

        if (hasDatabase)
        {
            questions.Add("How is the database configured?");
        }

        if (hasApi)
        {
            questions.Add("Which endpoints require authorization?");
        }

        // Add questions about key domain or application services
        var keyServices = chunks
            .Where(c => !string.IsNullOrWhiteSpace(c.TypeName) &&
                        (c.TypeName.EndsWith("Service", StringComparison.OrdinalIgnoreCase) ||
                         c.TypeName.EndsWith("Handler", StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.TypeName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();

        foreach (var svc in keyServices)
        {
            questions.Add($"What does {svc} do?");
        }

        if (questions.Count < 5)
        {
            questions.Add("What is the high-level architecture of this codebase?");
            questions.Add("Where are the core domain models defined?");
        }

        return questions.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
    }

    private static string? FormatRelativeTime(DateTime? dateTime)
    {
        if (!dateTime.HasValue) return null;
        var diff = DateTime.UtcNow - dateTime.Value;
        if (diff.TotalSeconds < 60) return "Just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return $"{(int)diff.TotalDays}d ago";
    }
}
