using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DevPilot.Application.AiProviders;
using DevPilot.Application.ProjectBrain.Models;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.ProjectBrain.Queries.SemanticSearch;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.ProjectBrain.Commands.AskBrain;

public sealed class AskBrainCommandHandler : IAskBrainCommandHandler
{
    private const int MaxRelevantChunks = 6;
    private const int MaxChunkCharsInPrompt = 1600;

    private readonly IRepositoryWorkspaceQuery _workspaceQuery;
    private readonly ICodeChunkRepository _chunkRepository;
    private readonly IIndexJobRepository _jobRepository;
    private readonly ISemanticSearchQueryHandler _searchHandler;
    private readonly IAiProvider _aiProvider;
    private readonly ILogger<AskBrainCommandHandler> _logger;

    public AskBrainCommandHandler(
        IRepositoryWorkspaceQuery workspaceQuery,
        ICodeChunkRepository chunkRepository,
        IIndexJobRepository jobRepository,
        ISemanticSearchQueryHandler searchHandler,
        IAiProvider aiProvider,
        ILogger<AskBrainCommandHandler> logger)
    {
        _workspaceQuery = workspaceQuery;
        _chunkRepository = chunkRepository;
        _jobRepository = jobRepository;
        _searchHandler = searchHandler;
        _aiProvider = aiProvider;
        _logger = logger;
    }

    public async Task<BrainChatResult> HandleAsync(
        AskBrainCommand command,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (command.WorkspaceId == Guid.Empty)
        {
            return new BrainChatResult
            {
                Success = false,
                ErrorMessage = "WorkspaceId is required.",
            };
        }

        if (string.IsNullOrWhiteSpace(command.Question))
        {
            return new BrainChatResult
            {
                Success = false,
                ErrorMessage = "Question cannot be empty.",
            };
        }

        var workspace = await _workspaceQuery
            .GetByIdAsync(command.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        if (workspace is null)
        {
            return new BrainChatResult
            {
                Success = false,
                ErrorMessage = $"Workspace {command.WorkspaceId} was not found.",
            };
        }

        if (workspace.Status != RepositoryWorkspaceStatus.Completed)
        {
            return new BrainChatResult
            {
                Success = false,
                IsUnindexed = true,
                ErrorMessage = $"Workspace is not ready (current status: {workspace.Status}).",
            };
        }

        // 1. Verify workspace has indexed chunks
        var chunkCount = await _chunkRepository
            .CountByWorkspaceAsync(workspace.Id, cancellationToken)
            .ConfigureAwait(false);

        if (chunkCount == 0)
        {
            return new BrainChatResult
            {
                Success = false,
                IsUnindexed = true,
                ErrorMessage = "Workspace has not been indexed yet. Please run an index job before asking questions.",
            };
        }

        // Check staleness
        var latestJob = await _jobRepository
            .GetLatestByWorkspaceAsync(workspace.Id, cancellationToken)
            .ConfigureAwait(false);

        var isStale = !string.IsNullOrEmpty(workspace.CommitSha) &&
                      !string.IsNullOrEmpty(latestJob?.CommitSha) &&
                      !string.Equals(workspace.CommitSha, latestJob.CommitSha, StringComparison.OrdinalIgnoreCase);

        // 2. Retrieve relevant chunks
        var searchQuery = new SemanticSearchQuery
        {
            RepositoryWorkspaceId = workspace.Id,
            WorkspacePath = workspace.LocalPath,
            QueryText = command.Question.Trim(),
            MaxResults = MaxRelevantChunks,
        };

        var searchResult = await _searchHandler
            .HandleAsync(searchQuery, cancellationToken)
            .ConfigureAwait(false);

        if (!searchResult.Success || searchResult.Hits.Count == 0)
        {
            stopwatch.Stop();
            return new BrainChatResult
            {
                Success = true,
                Role = "assistant",
                Content = "I could not find any relevant code snippets in the indexed workspace to answer your question. Please verify that the repository is indexed or try asking about specific classes, methods, or modules.",
                Confidence = 0,
                Elapsed = FormatDuration(stopwatch.Elapsed),
                RetrievalMode = searchResult.RetrievalMode,
                IsStale = isStale,
            };
        }

        // 3. Assign stable source IDs
        var sourceMap = new Dictionary<int, SemanticSearchHit>();
        var promptContextBuilder = new StringBuilder();
        int sourceIndex = 1;

        foreach (var hit in searchResult.Hits)
        {
            var chunk = hit.Chunk;
            sourceMap[sourceIndex] = hit;

            promptContextBuilder.AppendLine($"[Source {sourceIndex}: {chunk.RelativePath} (Lines {chunk.StartLine}-{chunk.EndLine}{(string.IsNullOrWhiteSpace(chunk.SymbolName) ? "" : $", Symbol: {chunk.SymbolName}")})]");
            promptContextBuilder.AppendLine($"```{FormatLang(chunk.Language)}");
            promptContextBuilder.AppendLine(Truncate(chunk.Content, MaxChunkCharsInPrompt));
            promptContextBuilder.AppendLine("```");
            promptContextBuilder.AppendLine();

            sourceIndex++;
        }

        // 4. Construct grounded system prompt and user prompt
        var systemPrompt = BuildSystemPrompt();
        var userPrompt = $"Repository Code Context:\n\n{promptContextBuilder}\nUser Question: {command.Question.Trim()}";

        var aiRequest = new AiRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
        };

        var aiResponse = await _aiProvider
            .SendAsync(aiRequest, cancellationToken)
            .ConfigureAwait(false);

        stopwatch.Stop();

        if (!aiResponse.IsSuccess)
        {
            return new BrainChatResult
            {
                Success = false,
                ErrorMessage = aiResponse.ErrorMessage ?? "AI provider failed to generate a response.",
                Elapsed = FormatDuration(stopwatch.Elapsed),
            };
        }

        // 5. Parse and strictly validate cited sources
        var rawContent = aiResponse.Content.Trim();
        var (cleanContent, citedSourceIds) = ExtractCitedSources(rawContent);

        var citations = new List<BrainCitationDto>();
        var citedHits = new List<SemanticSearchHit>();

        foreach (var id in citedSourceIds.Distinct())
        {
            if (sourceMap.TryGetValue(id, out var hit))
            {
                var chunk = hit.Chunk;
                citedHits.Add(hit);

                var linesStr = chunk.StartLine == chunk.EndLine
                    ? $"L{chunk.StartLine}"
                    : $"L{chunk.StartLine}–L{chunk.EndLine}";

                citations.Add(new BrainCitationDto
                {
                    File = Path.GetFileName(chunk.RelativePath),
                    Path = chunk.RelativePath,
                    Lines = linesStr,
                    StartLine = chunk.StartLine,
                    EndLine = chunk.EndLine,
                    Symbol = !string.IsNullOrWhiteSpace(chunk.SymbolName) ? chunk.SymbolName : chunk.TypeName,
                    Lang = FormatLang(chunk.Language),
                    Snippet = chunk.Content,
                });
            }
            else
            {
                _logger.LogWarning("Model returned invalid source ID [Source {SourceId}] not in retrieved context map", id);
            }
        }

        // Fallback citation detection if model referenced [Source X] in text without explicit SOURCES: line
        if (citations.Count == 0)
        {
            var inlineMatches = Regex.Matches(cleanContent, @"\[Source\s+(\d+)\]", RegexOptions.IgnoreCase);
            foreach (Match m in inlineMatches)
            {
                if (int.TryParse(m.Groups[1].Value, out var id) && sourceMap.TryGetValue(id, out var hit))
                {
                    if (citations.All(c => c.Path != hit.Chunk.RelativePath || c.StartLine != hit.Chunk.StartLine))
                    {
                        var chunk = hit.Chunk;
                        citedHits.Add(hit);

                        var linesStr = chunk.StartLine == chunk.EndLine
                            ? $"L{chunk.StartLine}"
                            : $"L{chunk.StartLine}–L{chunk.EndLine}";

                        citations.Add(new BrainCitationDto
                        {
                            File = Path.GetFileName(chunk.RelativePath),
                            Path = chunk.RelativePath,
                            Lines = linesStr,
                            StartLine = chunk.StartLine,
                            EndLine = chunk.EndLine,
                            Symbol = !string.IsNullOrWhiteSpace(chunk.SymbolName) ? chunk.SymbolName : chunk.TypeName,
                            Lang = FormatLang(chunk.Language),
                            Snippet = chunk.Content,
                        });
                    }
                }
            }
        }

        // 6. Build ContextFiles for the Context Used panel
        var contextFiles = searchResult.Hits
            .GroupBy(h => h.Chunk.RelativePath)
            .Select(g =>
            {
                var first = g.First();
                var maxScore = g.Max(h => h.Score);
                var dir = Path.GetDirectoryName(first.Chunk.RelativePath)?.Replace('\\', '/') ?? "";
                return new BrainContextFileDto
                {
                    File = Path.GetFileName(first.Chunk.RelativePath),
                    Path = string.IsNullOrWhiteSpace(dir) ? first.Chunk.RelativePath : dir,
                    Relevance = Math.Clamp((int)Math.Round(maxScore * 100), 1, 99),
                };
            })
            .OrderByDescending(f => f.Relevance)
            .ToList();

        // 7. Calculate Grounding Score (objective signal based on retrieval relevance of cited sources)
        int? groundingScore = null;
        if (citedHits.Count > 0)
        {
            var avgScore = citedHits.Average(h => h.Score);
            groundingScore = Math.Clamp((int)Math.Round(avgScore * 100), 65, 98);
        }
        else if (citations.Count > 0)
        {
            groundingScore = 75;
        }

        return new BrainChatResult
        {
            Success = true,
            Role = "assistant",
            Content = cleanContent,
            Confidence = groundingScore,
            Elapsed = FormatDuration(stopwatch.Elapsed),
            Citations = citations,
            ContextFiles = contextFiles,
            RetrievalMode = searchResult.RetrievalMode,
            IsStale = isStale,
        };
    }

    private static string BuildSystemPrompt()
    {
        return @"You are Project Brain, an expert software architecture and code intelligence assistant.
Your task is to answer the user's question accurately and truthfully based STRICTLY on the provided repository code excerpts.

Rules:
1. Base your answer ONLY on the provided code sources.
2. Do NOT invent, assume, or hallucinate APIs, methods, file paths, or line numbers not shown in the excerpts.
3. If the excerpts do not contain enough information to fully answer, state clearly what is known from the excerpts and what cannot be determined.
4. At the very end of your response, on a new line, you MUST list the exact source IDs you directly used to answer, in the format:
SOURCES: [Source 1], [Source 2]
(If no sources were used, write SOURCES: None)";
    }

    private static (string CleanContent, List<int> SourceIds) ExtractCitedSources(string rawContent)
    {
        var sourceIds = new List<int>();

        var match = Regex.Match(rawContent, @"(?:\r?\n|^)SOURCES:\s*(.*)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var sourcesLine = match.Groups[1].Value;
            var cleanText = rawContent.Substring(0, match.Index).TrimEnd();

            var idMatches = Regex.Matches(sourcesLine, @"\[Source\s+(\d+)\]|\bSource\s+(\d+)\b|\b(\d+)\b", RegexOptions.IgnoreCase);
            foreach (Match m in idMatches)
            {
                var val = m.Groups[1].Success ? m.Groups[1].Value :
                          m.Groups[2].Success ? m.Groups[2].Value :
                          m.Groups[3].Value;

                if (int.TryParse(val, out var id))
                {
                    sourceIds.Add(id);
                }
            }

            return (cleanText, sourceIds);
        }

        return (rawContent, sourceIds);
    }

    private static string FormatLang(string lang)
    {
        return lang.ToLowerInvariant() switch
        {
            "csharp" => "cs",
            "typescript" => "ts",
            "javascript" => "js",
            "xml" => "xml",
            "json" => "json",
            "markdown" => "md",
            _ => "cs",
        };
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        return text.Substring(0, maxLength) + "\n// ... [truncated]";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
        {
            return $"{duration.TotalMilliseconds:F0}ms";
        }
        return $"{duration.TotalSeconds:F1}s";
    }
}
