using System.Collections.Concurrent;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Application.RepositoryWorkspaces.Ports;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.RepositoryWorkspaces.Queries.GetRepositoryWorkspaceAnalysis;

public sealed class GetRepositoryWorkspaceAnalysisQueryHandler : IGetRepositoryWorkspaceAnalysisQueryHandler
{
    private static readonly ConcurrentDictionary<string, WorkspaceAnalysisDto> AnalysisCache = new();
    private static readonly ConcurrentDictionary<Guid, WorkspaceAnalysisDto> LastKnownGoodCache = new();

    private readonly IRepositoryWorkspaceQuery _workspaceQuery;
    private readonly IRepositoryAnalyzer _analyzer;
    private readonly IRepositoryStructureScanner _structureScanner;
    private readonly ILogger<GetRepositoryWorkspaceAnalysisQueryHandler> _logger;

    public GetRepositoryWorkspaceAnalysisQueryHandler(
        IRepositoryWorkspaceQuery workspaceQuery,
        IRepositoryAnalyzer analyzer,
        IRepositoryStructureScanner structureScanner,
        ILogger<GetRepositoryWorkspaceAnalysisQueryHandler> logger)
    {
        _workspaceQuery = workspaceQuery;
        _analyzer = analyzer;
        _structureScanner = structureScanner;
        _logger = logger;
    }

    public static void ClearCache()
    {
        AnalysisCache.Clear();
        LastKnownGoodCache.Clear();
    }

    public async Task<GetRepositoryWorkspaceAnalysisResult> HandleAsync(
        GetRepositoryWorkspaceAnalysisQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query is null)
        {
            return new GetRepositoryWorkspaceAnalysisResult
            {
                Success = false,
                ErrorMessage = "Query is required.",
            };
        }

        var workspace = await _workspaceQuery
            .GetByIdAsync(query.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        if (workspace is null)
        {
            return new GetRepositoryWorkspaceAnalysisResult
            {
                Success = false,
                NotFound = true,
                ErrorMessage = "Repository workspace not found.",
            };
        }

        if (workspace.Status != RepositoryWorkspaceStatus.Completed)
        {
            return new GetRepositoryWorkspaceAnalysisResult
            {
                Success = false,
                IsConflict = true,
                ErrorMessage = $"Repository workspace is not ready for analysis (status: {workspace.Status}).",
            };
        }

        if (string.IsNullOrWhiteSpace(workspace.LocalPath) || !Directory.Exists(workspace.LocalPath))
        {
            return new GetRepositoryWorkspaceAnalysisResult
            {
                Success = false,
                IsConflict = true,
                ErrorMessage = "Local repository directory does not exist or is unavailable.",
            };
        }

        var cacheKey = $"{workspace.Id}:{workspace.CommitSha ?? "head"}:v1";

        if (!query.ForceRecompute && AnalysisCache.TryGetValue(cacheKey, out var cachedAnalysis))
        {
            _logger.LogInformation("Repository workspace analysis cache HIT for workspace {WorkspaceId}, commit {CommitSha}.", workspace.Id, workspace.CommitSha);
            return new GetRepositoryWorkspaceAnalysisResult
            {
                Success = true,
                Analysis = cachedAnalysis,
            };
        }

        var recomputeReason = query.ForceRecompute
            ? "force recompute requested"
            : (!AnalysisCache.ContainsKey(cacheKey) ? "not in cache" : "commit changed");

        _logger.LogInformation("Repository workspace analysis cache MISS for workspace {WorkspaceId}, commit {CommitSha}. Recompute reason: {Reason}.", workspace.Id, workspace.CommitSha, recomputeReason);

        var rootPath = Path.GetFullPath(workspace.LocalPath);

        RepositoryAnalysisResult roslynResult;
        try
        {
            roslynResult = await _analyzer.AnalyzeAsync(
                new RepositoryAnalysisRequest { WorkspacePath = rootPath },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (LastKnownGoodCache.TryGetValue(workspace.Id, out var lkg))
            {
                _logger.LogWarning(ex, "Roslyn analysis failed for workspace {WorkspaceId}, returning last-known-good cached analysis.", workspace.Id);
                return new GetRepositoryWorkspaceAnalysisResult
                {
                    Success = true,
                    Analysis = lkg,
                };
            }

            _logger.LogError(ex, "Roslyn analysis failed for workspace {WorkspaceId} at {Path}", workspace.Id, rootPath);
            return new GetRepositoryWorkspaceAnalysisResult
            {
                Success = false,
                ErrorMessage = $"Code analysis failed: {ex.Message}",
            };
        }

        var fileTree = await _structureScanner.ScanStructureAsync(rootPath, cancellationToken)
            .ConfigureAwait(false);

        var technologies = await _structureScanner.DetectTechnologiesAsync(rootPath, roslynResult, cancellationToken)
            .ConfigureAwait(false);

        var analysisDto = await ProjectAnalysisResultAsync(
            workspace,
            rootPath,
            roslynResult,
            fileTree,
            technologies,
            cancellationToken).ConfigureAwait(false);

        AnalysisCache[cacheKey] = analysisDto;
        LastKnownGoodCache[workspace.Id] = analysisDto;

        // Clean up older stale commit entries for this workspace
        foreach (var key in AnalysisCache.Keys)
        {
            if (key.StartsWith($"{workspace.Id}:", StringComparison.Ordinal) && key != cacheKey)
            {
                AnalysisCache.TryRemove(key, out _);
            }
        }

        return new GetRepositoryWorkspaceAnalysisResult
        {
            Success = true,
            Analysis = analysisDto,
        };
    }

    private async Task<WorkspaceAnalysisDto> ProjectAnalysisResultAsync(
        Domain.Entities.RepositoryWorkspace workspace,
        string rootPath,
        RepositoryAnalysisResult roslynResult,
        List<WorkspaceFileNodeDto> fileTree,
        List<WorkspaceTechnologyDto> technologies,
        CancellationToken cancellationToken)
    {
        var rawProjects = new List<ProjectAnalysisResult>();

        foreach (var solution in roslynResult.Solutions)
        {
            rawProjects.AddRange(solution.Projects);
        }
        rawProjects.AddRange(roslynResult.StandaloneProjects);

        // Deduplicate projects by path
        var distinctProjects = rawProjects
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Path) ? p.Name : p.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var projectDtos = new List<WorkspaceProjectDto>();
        var endpointDtos = new List<WorkspaceEndpointDto>();
        var sanitizedWarnings = new List<string>();

        var totalSymbols = 0;
        var totalTypes = 0;
        var totalReferences = 0;

        foreach (var warning in roslynResult.Warnings)
        {
            sanitizedWarnings.Add(SanitizePath(warning, rootPath));
        }

        foreach (var p in distinctProjects)
        {
            var fileCount = await _structureScanner.CountProjectFilesAsync(p.Path, rootPath, cancellationToken)
                .ConfigureAwait(false);

            var projDto = new WorkspaceProjectDto
            {
                Name = p.Name,
                Path = MakeRelative(p.Path, rootPath),
                ProjectType = p.ProjectType,
                Layer = DetermineLayer(p.Name, p.ProjectType),
                FileCount = fileCount,
                TargetFramework = p.TargetFramework,
                CompilationSucceeded = p.CompilationSucceeded,
                CompilationErrors = p.CompilationErrors.Select(e => SanitizePath(e, rootPath)).ToList(),
                Warnings = p.Warnings.Select(w => SanitizePath(w, rootPath)).ToList(),
                ProjectReferences = p.ProjectReferences
                    .Select(r => new WorkspaceProjectReferenceDto
                    {
                        Name = r.Name,
                        Path = MakeRelative(r.Path, rootPath),
                    })
                    .ToList(),
            };

            projectDtos.Add(projDto);

            // Metrics calculation
            totalReferences += p.ProjectReferences.Count;

            // Types: Classes, Interfaces, Records, Enums, Controllers
            var projectTypeCount = p.Classes.Count + p.Interfaces.Count + p.Records.Count + p.Enums.Count + p.Controllers.Count;
            totalTypes += projectTypeCount;

            // Symbols: Methods, Constructors, Properties across all types + Enum members
            foreach (var c in p.Classes)
            {
                totalSymbols += c.Methods.Count + c.Constructors.Count + c.Properties.Count;
            }
            foreach (var i in p.Interfaces)
            {
                totalSymbols += i.Methods.Count + i.Constructors.Count + i.Properties.Count;
            }
            foreach (var r in p.Records)
            {
                totalSymbols += r.Methods.Count + r.Constructors.Count + r.Properties.Count;
            }
            foreach (var e in p.Enums)
            {
                totalSymbols += e.Values.Count;
            }
            foreach (var ctrl in p.Controllers)
            {
                totalSymbols += ctrl.Methods.Count + ctrl.Constructors.Count + ctrl.Properties.Count;

                foreach (var action in ctrl.Actions)
                {
                    endpointDtos.Add(new WorkspaceEndpointDto
                    {
                        Method = string.IsNullOrWhiteSpace(action.HttpMethod) ? "GET" : action.HttpMethod.ToUpperInvariant(),
                        Route = NormalizeRoute(action.RouteTemplate, ctrl.Name, action.Name),
                        Controller = ctrl.Name,
                        Action = action.Name,
                        Auth = action.IsAuthorized,
                        SourcePath = MakeRelative(action.SourcePath, rootPath),
                    });
                }
            }
        }

        // Deduplicate and order endpoints
        endpointDtos = endpointDtos
            .GroupBy(e => $"{e.Method} {e.Route} {e.Controller}.{e.Action}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.Route, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Method, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allCompilationSucceeded = projectDtos.Count > 0 && projectDtos.All(p => p.CompilationSucceeded);
        var status = !roslynResult.Success
            ? "Failed"
            : (allCompilationSucceeded && sanitizedWarnings.Count == 0 ? "Ready" : "Partial");

        var hasDiscoveredProjects = roslynResult.Solutions.Count > 0 || roslynResult.StandaloneProjects.Count > 0;
        var steps = new List<WorkspaceAnalysisStepDto>
        {
            new() { Label = "Load solution & projects", Done = hasDiscoveredProjects },
            new() { Label = "Compile & parse source", Done = roslynResult.Success && (projectDtos.Count == 0 || allCompilationSucceeded) },
            new() { Label = "Extract types & symbols", Done = roslynResult.Success },
            new() { Label = "Resolve project references", Done = roslynResult.Success },
            new() { Label = "Scan structure & technologies", Done = fileTree.Count > 0 || technologies.Count > 0 },
        };

        return new WorkspaceAnalysisDto
        {
            Repository = new WorkspaceRepositoryInfoDto
            {
                Owner = workspace.Owner,
                Repository = workspace.Repository,
                FullName = $"{workspace.Owner}/{workspace.Repository}",
                Branch = workspace.Branch,
                CommitSha = workspace.CommitSha,
            },
            Summary = new WorkspaceAnalysisSummaryDto
            {
                Status = status,
                Engine = "Roslyn workspace analysis",
                SymbolsCount = totalSymbols,
                TypesCount = totalTypes,
                ReferencesCount = totalReferences,
                AnalyzedAt = DateTime.UtcNow,
                Steps = steps,
            },
            FileTree = fileTree,
            Projects = projectDtos.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Technologies = technologies,
            Endpoints = endpointDtos,
            Warnings = sanitizedWarnings,
        };
    }

    private static string NormalizeRoute(string? routeTemplate, string controllerName, string actionName)
    {
        if (string.IsNullOrWhiteSpace(routeTemplate))
        {
            var shortCtrl = controllerName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
                ? controllerName[..^10]
                : controllerName;
            return $"/api/{shortCtrl.ToLowerInvariant()}/{actionName.ToLowerInvariant()}";
        }

        var route = routeTemplate.Trim();
        if (!route.StartsWith('/'))
        {
            route = "/" + route;
        }

        return route;
    }

    private static string DetermineLayer(string projectName, string projectType)
    {
        var lower = projectName.ToLowerInvariant();

        if (lower.EndsWith(".tests") || lower.EndsWith(".test") || lower.Contains("unittest") || lower.Contains("integrationtest"))
        {
            return "Tests";
        }

        if (lower.EndsWith(".api") || lower.EndsWith(".web") || projectType.Equals("Web", StringComparison.OrdinalIgnoreCase))
        {
            return "Web";
        }

        if (lower.EndsWith(".application") || lower.Contains(".app"))
        {
            return "Application";
        }

        if (lower.EndsWith(".domain") || lower.EndsWith(".core"))
        {
            return "Domain";
        }

        if (lower.EndsWith(".infrastructure") || lower.EndsWith(".persistence") || lower.EndsWith(".data"))
        {
            return "Infrastructure";
        }

        return "Application";
    }

    private static string MakeRelative(string? path, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedPath = Path.GetFullPath(path);

            if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
                return relative.Replace('\\', '/');
            }

            return Path.GetFileName(normalizedPath);
        }
        catch
        {
            return Path.GetFileName(path) ?? string.Empty;
        }
    }

    private static string SanitizePath(string text, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var forwardRoot = normalizedRoot.Replace('\\', '/');
        var backwardRoot = normalizedRoot.Replace('/', '\\');

        var sanitized = text
            .Replace(backwardRoot + "\\", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(backwardRoot, string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(forwardRoot + "/", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(forwardRoot, string.Empty, StringComparison.OrdinalIgnoreCase);

        return sanitized.Replace('\\', '/');
    }
}
