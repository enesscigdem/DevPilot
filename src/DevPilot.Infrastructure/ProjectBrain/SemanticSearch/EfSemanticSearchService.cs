using System.Text.RegularExpressions;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Domain.ProjectBrain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace DevPilot.Infrastructure.ProjectBrain.SemanticSearch;

public sealed class EfSemanticSearchService : ISemanticSearchService
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "in", "on", "at", "to", "for", "of", "with", "by", "from",
        "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did",
        "how", "what", "where", "when", "why", "which", "who", "whom",
        "can", "could", "should", "would", "will", "shall",
        "work", "works", "use", "uses", "used", "using",
    };

    private readonly DevPilotDbContext _context;
    private readonly ILogger<EfSemanticSearchService> _logger;

    public EfSemanticSearchService(
        DevPilotDbContext context,
        ILogger<EfSemanticSearchService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<SemanticSearchResult> SearchAsync(
        SemanticSearchQuery query,
        float[]? queryEmbedding,
        CancellationToken cancellationToken = default)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        if (string.IsNullOrWhiteSpace(query.QueryText))
        {
            return new SemanticSearchResult
            {
                Success = false,
                ErrorMessage = "Query text is required.",
            };
        }

        // 1. Attempt vector search if embedding is present and database is Npgsql
        if (queryEmbedding is not null && queryEmbedding.Length > 0)
        {
            try
            {
                var vectorResults = await SearchVectorAsync(query, queryEmbedding, cancellationToken).ConfigureAwait(false);
                if (vectorResults.Hits.Count > 0)
                {
                    return vectorResults;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vector search failed; falling back to lexical search for query: {Query}", query.QueryText);
            }
        }

        // 2. Lexical / Symbol workspace-scoped fallback
        return await SearchLexicalAsync(query, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SemanticSearchResult> SearchVectorAsync(
        SemanticSearchQuery query,
        float[] queryEmbedding,
        CancellationToken cancellationToken)
    {
        var targetVector = new Vector(queryEmbedding);
        var baseQuery = _context.CodeChunks.AsNoTracking();

        if (query.RepositoryWorkspaceId.HasValue)
        {
            baseQuery = baseQuery.Where(c => c.RepositoryWorkspaceId == query.RepositoryWorkspaceId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(query.WorkspacePath))
        {
            baseQuery = baseQuery.Where(c => c.WorkspacePath == query.WorkspacePath);
        }

        baseQuery = baseQuery.Where(c => c.Embedding != null);

        var hits = await baseQuery
            .Select(c => new
            {
                Chunk = c,
                Distance = c.Embedding!.CosineDistance(targetVector),
            })
            .OrderBy(x => x.Distance)
            .Take(query.MaxResults * 2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var filteredHits = new List<SemanticSearchHit>();
        foreach (var item in hits)
        {
            var similarity = Math.Clamp(1.0 - item.Distance, 0.0, 1.0);
            if (query.MinScore.HasValue && similarity < query.MinScore.Value)
            {
                continue;
            }

            filteredHits.Add(new SemanticSearchHit
            {
                Chunk = item.Chunk,
                Score = similarity,
            });

            if (filteredHits.Count >= query.MaxResults)
            {
                break;
            }
        }

        return new SemanticSearchResult
        {
            Success = true,
            Hits = filteredHits,
            RetrievalMode = "vector",
        };
    }

    private async Task<SemanticSearchResult> SearchLexicalAsync(
        SemanticSearchQuery query,
        CancellationToken cancellationToken)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Regex.Matches(query.QueryText, @"[A-Za-z0-9_#]+").Cast<Match>())
        {
            var word = m.Value.Trim();
            if (word.Length >= 2 && !StopWords.Contains(word))
            {
                tokens.Add(word);
                if (word.EndsWith("tion", StringComparison.OrdinalIgnoreCase) && word.Length > 6)
                {
                    tokens.Add(word.Substring(0, word.Length - 4));
                    if (word.Length >= 4) tokens.Add(word.Substring(0, 4));
                }
                else if (word.EndsWith("ing", StringComparison.OrdinalIgnoreCase) && word.Length > 5)
                {
                    tokens.Add(word.Substring(0, word.Length - 3));
                }
                else if (word.EndsWith("ed", StringComparison.OrdinalIgnoreCase) && word.Length > 4)
                {
                    tokens.Add(word.Substring(0, word.Length - 2));
                }
                else if (word.EndsWith("s", StringComparison.OrdinalIgnoreCase) && word.Length > 3)
                {
                    tokens.Add(word.Substring(0, word.Length - 1));
                }
            }
        }

        if (tokens.Count == 0)
        {
            foreach (var m in Regex.Matches(query.QueryText, @"\w+").Cast<Match>())
            {
                var word = m.Value.Trim();
                if (word.Length >= 1) tokens.Add(word);
            }
        }

        var rawTokens = tokens.ToList();

        var baseQuery = _context.CodeChunks.AsNoTracking();
        if (query.RepositoryWorkspaceId.HasValue)
        {
            baseQuery = baseQuery.Where(c => c.RepositoryWorkspaceId == query.RepositoryWorkspaceId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(query.WorkspacePath))
        {
            baseQuery = baseQuery.Where(c => c.WorkspacePath == query.WorkspacePath);
        }

        // Bounded database fetch: pull candidate chunks that match any token in symbol, path, or content
        var candidateList = new List<CodeChunk>();
        const int maxCandidates = 80;

        foreach (var token in rawTokens.Take(6))
        {
            var tokenMatches = await baseQuery
                .Where(c =>
                    (c.SymbolName != null && c.SymbolName.Contains(token)) ||
                    (c.TypeName != null && c.TypeName.Contains(token)) ||
                    (c.MethodName != null && c.MethodName.Contains(token)) ||
                    (c.DeclaredSymbols != null && c.DeclaredSymbols.Contains(token)) ||
                    c.RelativePath.Contains(token) ||
                    c.Content.Contains(token))
                .Take(30)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var match in tokenMatches)
            {
                if (candidateList.All(c => c.Id != match.Id))
                {
                    candidateList.Add(match);
                    if (candidateList.Count >= maxCandidates)
                    {
                        break;
                    }
                }
            }

            if (candidateList.Count >= maxCandidates)
            {
                break;
            }
        }

        if (candidateList.Count == 0)
        {
            // If no candidate matches token filter, take top few chunks from workspace as baseline fallback
            candidateList = await baseQuery
                .OrderBy(c => c.ChunkOrder)
                .Take(query.MaxResults)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        // Score candidates
        var scoredList = new List<(CodeChunk Chunk, double RawScore)>();

        foreach (var chunk in candidateList)
        {
            double rawScore = 0.0;
            foreach (var token in rawTokens)
            {
                if (!string.IsNullOrWhiteSpace(chunk.SymbolName) &&
                    chunk.SymbolName.Equals(token, StringComparison.OrdinalIgnoreCase))
                {
                    rawScore += 20.0;
                }
                else if (!string.IsNullOrWhiteSpace(chunk.SymbolName) &&
                         chunk.SymbolName.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    rawScore += 10.0;
                }

                if (!string.IsNullOrWhiteSpace(chunk.TypeName) &&
                    chunk.TypeName.Equals(token, StringComparison.OrdinalIgnoreCase))
                {
                    rawScore += 15.0;
                }
                else if (!string.IsNullOrWhiteSpace(chunk.TypeName) &&
                         chunk.TypeName.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    rawScore += 8.0;
                }

                if (!string.IsNullOrWhiteSpace(chunk.MethodName) &&
                    chunk.MethodName.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    rawScore += 7.0;
                }

                if (chunk.RelativePath.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    rawScore += 6.0;
                }

                if (!string.IsNullOrWhiteSpace(chunk.DeclaredSymbols) &&
                    chunk.DeclaredSymbols.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    rawScore += 5.0;
                }

                var contentCount = CountOccurrences(chunk.Content, token);
                rawScore += Math.Min(contentCount * 1.5, 6.0);
            }

            scoredList.Add((chunk, rawScore));
        }

        var maxRaw = scoredList.Count > 0 ? scoredList.Max(x => x.RawScore) : 0.0;

        var hits = scoredList
            .OrderByDescending(x => x.RawScore)
            .Take(query.MaxResults)
            .Select(x =>
            {
                // Normalize to a 0.50 - 0.96 scale for meaningful presentation
                double normalizedScore = maxRaw > 0.0
                    ? 0.50 + (x.RawScore / maxRaw) * 0.46
                    : 0.50;

                return new SemanticSearchHit
                {
                    Chunk = x.Chunk,
                    Score = Math.Round(normalizedScore, 3),
                };
            })
            .ToList();

        return new SemanticSearchResult
        {
            Success = true,
            Hits = hits,
            RetrievalMode = "lexical",
        };
    }

    private static int CountOccurrences(string text, string pattern)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern)) return 0;
        int count = 0;
        int i = 0;
        while ((i = text.IndexOf(pattern, i, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            i += pattern.Length;
            count++;
            if (count >= 10) break;
        }
        return count;
    }
}
