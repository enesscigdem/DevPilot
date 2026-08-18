using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetRepositoryWorkspaceAnalysis;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.RepositoryWorkspaces.Queries.GetRepositoryWorkspaceArchitecture;

public sealed class GetRepositoryWorkspaceArchitectureQueryHandler : IGetRepositoryWorkspaceArchitectureQueryHandler
{
    private readonly IGetRepositoryWorkspaceAnalysisQueryHandler _analysisQueryHandler;
    private readonly ILogger<GetRepositoryWorkspaceArchitectureQueryHandler> _logger;

    public GetRepositoryWorkspaceArchitectureQueryHandler(
        IGetRepositoryWorkspaceAnalysisQueryHandler analysisQueryHandler,
        ILogger<GetRepositoryWorkspaceArchitectureQueryHandler> logger)
    {
        _analysisQueryHandler = analysisQueryHandler;
        _logger = logger;
    }

    public async Task<GetRepositoryWorkspaceArchitectureResult> HandleAsync(
        GetRepositoryWorkspaceArchitectureQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query is null)
        {
            return new GetRepositoryWorkspaceArchitectureResult
            {
                Success = false,
                ErrorMessage = "Query is required.",
            };
        }

        var analysisResult = await _analysisQueryHandler
            .HandleAsync(new GetRepositoryWorkspaceAnalysisQuery(query.WorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        if (analysisResult.NotFound)
        {
            return new GetRepositoryWorkspaceArchitectureResult
            {
                Success = false,
                NotFound = true,
                ErrorMessage = analysisResult.ErrorMessage ?? "Repository workspace not found.",
            };
        }

        if (analysisResult.IsConflict)
        {
            return new GetRepositoryWorkspaceArchitectureResult
            {
                Success = false,
                IsConflict = true,
                ErrorMessage = analysisResult.ErrorMessage,
            };
        }

        if (!analysisResult.Success || analysisResult.Analysis is null)
        {
            return new GetRepositoryWorkspaceArchitectureResult
            {
                Success = false,
                ErrorMessage = analysisResult.ErrorMessage ?? "Failed to analyze repository workspace architecture.",
            };
        }

        var analysis = analysisResult.Analysis;
        var architecture = BuildArchitectureGraph(analysis);

        return new GetRepositoryWorkspaceArchitectureResult
        {
            Success = true,
            Architecture = architecture,
        };
    }

    private static WorkspaceArchitectureDto BuildArchitectureGraph(WorkspaceAnalysisDto analysis)
    {
        var nodes = new List<WorkspaceArchitectureNodeDto>();
        var edges = new List<WorkspaceArchitectureEdgeDto>();

        var allFilePaths = new List<string>();
        CollectFilePaths(analysis.FileTree, allFilePaths);

        var hasMediatR = analysis.Technologies.Any(t => t.Name.Equals("MediatR", StringComparison.OrdinalIgnoreCase));
        var hasEfCore = analysis.Technologies.Any(t => t.Name.Equals("Entity Framework Core", StringComparison.OrdinalIgnoreCase));

        // 1. Build .NET Project Nodes
        foreach (var project in analysis.Projects)
        {
            var projectDir = GetProjectDirectory(project.Path);
            var projectFiles = allFilePaths
                .Where(p => string.IsNullOrWhiteSpace(projectDir) || p.StartsWith(projectDir + "/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var keyFiles = SelectDeterministicKeyFiles(project, projectFiles, analysis.Endpoints);
            var sub = DeriveProjectSubtitle(project, hasMediatR, hasEfCore);

            var node = new WorkspaceArchitectureNodeDto
            {
                Id = project.Name,
                Label = project.Name,
                Sub = sub,
                Layer = project.Layer,
                ProjectType = project.ProjectType,
                Path = project.Path,
                KeyFiles = keyFiles,
                Impacted = false,
                Why = string.Empty,
            };

            nodes.Add(node);
        }

        // 2. Build ProjectReference Edges (A -> B means A depends on B)
        foreach (var project in analysis.Projects)
        {
            foreach (var reference in project.ProjectReferences)
            {
                var targetProject = analysis.Projects.FirstOrDefault(p =>
                    p.Name.Equals(reference.Name, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(reference.Path) && p.Path.Equals(reference.Path, StringComparison.OrdinalIgnoreCase)));

                if (targetProject is not null && !targetProject.Name.Equals(project.Name, StringComparison.OrdinalIgnoreCase))
                {
                    edges.Add(new WorkspaceArchitectureEdgeDto
                    {
                        From = project.Name,
                        To = targetProject.Name,
                        Type = "ProjectReference",
                    });
                }
            }
        }

        // 3. Detect Real External / Infrastructure Dependencies
        DetectExternalDependencies(analysis, nodes, edges, allFilePaths);

        // 4. Detect Frontend Presentation Project if present
        DetectFrontendProject(analysis, nodes, edges, allFilePaths);

        // 5. Populate Incoming ("Depended on by") and Outgoing ("Depends on") for each node
        // Deduplicate edges first
        var distinctEdges = edges
            .GroupBy(e => $"{e.From}->{e.To}:{e.Type}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        foreach (var node in nodes)
        {
            node.Outgoing = distinctEdges
                .Where(e => e.From.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.To)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            node.Incoming = distinctEdges
                .Where(e => e.To.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.From)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return new WorkspaceArchitectureDto
        {
            Repository = analysis.Repository,
            Summary = new WorkspaceArchitectureSummaryDto
            {
                Status = analysis.Summary.Status,
                NodesCount = nodes.Count,
                EdgesCount = distinctEdges.Count,
                AnalyzedAt = DateTime.UtcNow,
            },
            Nodes = nodes,
            Edges = distinctEdges,
        };
    }

    private static void DetectExternalDependencies(
        WorkspaceAnalysisDto analysis,
        List<WorkspaceArchitectureNodeDto> nodes,
        List<WorkspaceArchitectureEdgeDto> edges,
        List<string> allFilePaths)
    {
        // Helper to find projects that actually reference/use a given provider package
        List<WorkspaceArchitectureNodeDto> GetReferencingProjects(Func<string, bool> packageMatcher, string? keyPattern)
        {
            var matched = new List<WorkspaceArchitectureNodeDto>();

            foreach (var project in analysis.Projects)
            {
                var isReferenced = false;

                // 1. Check if the project file on disk exists and contains the package reference
                if (!string.IsNullOrWhiteSpace(project.Path) && File.Exists(project.Path))
                {
                    try
                    {
                        var content = File.ReadAllText(project.Path);
                        if (packageMatcher(content))
                        {
                            isReferenced = true;
                        }
                    }
                    catch
                    {
                        // Ignore file read error and proceed
                    }
                }

                // 2. If csproj could not be read directly, check if project has keyFiles matching database pattern
                if (!isReferenced && !string.IsNullOrWhiteSpace(keyPattern))
                {
                    var projDir = GetProjectDirectory(project.Path);
                    if (allFilePaths.Any(f => (string.IsNullOrWhiteSpace(projDir) || f.StartsWith(projDir + "/", StringComparison.OrdinalIgnoreCase)) &&
                                              f.Contains(keyPattern, StringComparison.OrdinalIgnoreCase)))
                    {
                        isReferenced = true;
                    }
                }

                if (isReferenced)
                {
                    var node = nodes.FirstOrDefault(n => n.Id.Equals(project.Name, StringComparison.OrdinalIgnoreCase));
                    if (node is not null)
                    {
                        matched.Add(node);
                    }
                }
            }

            return matched;
        }

        // PostgreSQL: connect only to projects that actually reference Npgsql
        if (analysis.Technologies.Any(t => t.Name.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase)))
        {
            var referencingProjects = GetReferencingProjects(
                content => content.Contains("Npgsql", StringComparison.OrdinalIgnoreCase),
                "DbContext.cs");

            if (referencingProjects.Count > 0)
            {
                var dbKeyFiles = allFilePaths
                    .Where(f => f.EndsWith("DbContext.cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith("Context.cs", StringComparison.OrdinalIgnoreCase))
                    .Take(2)
                    .ToList();

                var postgresNode = new WorkspaceArchitectureNodeDto
                {
                    Id = "postgres",
                    Label = "PostgreSQL",
                    Sub = "Relational Database",
                    Layer = "Data",
                    ProjectType = "Database",
                    Path = string.Empty,
                    KeyFiles = dbKeyFiles,
                    Impacted = false,
                    Why = string.Empty,
                };

                nodes.Add(postgresNode);

                foreach (var proj in referencingProjects)
                {
                    edges.Add(new WorkspaceArchitectureEdgeDto
                    {
                        From = proj.Id,
                        To = postgresNode.Id,
                        Type = "DatabaseConnection",
                    });
                }
            }
        }

        // Redis: connect only to projects that actually reference StackExchange.Redis
        if (analysis.Technologies.Any(t => t.Name.Equals("Redis", StringComparison.OrdinalIgnoreCase)))
        {
            var referencingProjects = GetReferencingProjects(
                content => content.Contains("StackExchange.Redis", StringComparison.OrdinalIgnoreCase) ||
                           content.Contains("Microsoft.Extensions.Caching.StackExchangeRedis", StringComparison.OrdinalIgnoreCase),
                "Redis");

            if (referencingProjects.Count > 0)
            {
                var redisNode = new WorkspaceArchitectureNodeDto
                {
                    Id = "redis",
                    Label = "Redis",
                    Sub = "Cache · Key-Value Store",
                    Layer = "Data",
                    ProjectType = "Database",
                    Path = string.Empty,
                    KeyFiles = new List<string>(),
                    Impacted = false,
                    Why = string.Empty,
                };

                nodes.Add(redisNode);

                foreach (var proj in referencingProjects)
                {
                    edges.Add(new WorkspaceArchitectureEdgeDto
                    {
                        From = proj.Id,
                        To = redisNode.Id,
                        Type = "CacheConnection",
                    });
                }
            }
        }

        // SQL Server: connect only to projects that actually reference SqlServer / SqlClient
        if (analysis.Technologies.Any(t => t.Name.Equals("SQL Server", StringComparison.OrdinalIgnoreCase)))
        {
            var referencingProjects = GetReferencingProjects(
                content => content.Contains("Microsoft.EntityFrameworkCore.SqlServer", StringComparison.OrdinalIgnoreCase) ||
                           content.Contains("Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase) ||
                           content.Contains("System.Data.SqlClient", StringComparison.OrdinalIgnoreCase),
                "SqlServer");

            if (referencingProjects.Count > 0)
            {
                var sqlServerNode = new WorkspaceArchitectureNodeDto
                {
                    Id = "sqlserver",
                    Label = "SQL Server",
                    Sub = "Relational Database",
                    Layer = "Data",
                    ProjectType = "Database",
                    Path = string.Empty,
                    KeyFiles = new List<string>(),
                    Impacted = false,
                    Why = string.Empty,
                };

                nodes.Add(sqlServerNode);

                foreach (var proj in referencingProjects)
                {
                    edges.Add(new WorkspaceArchitectureEdgeDto
                    {
                        From = proj.Id,
                        To = sqlServerNode.Id,
                        Type = "DatabaseConnection",
                    });
                }
            }
        }

        // SQLite: connect only to projects that actually reference Sqlite
        if (analysis.Technologies.Any(t => t.Name.Equals("SQLite", StringComparison.OrdinalIgnoreCase)))
        {
            var referencingProjects = GetReferencingProjects(
                content => content.Contains("Microsoft.EntityFrameworkCore.Sqlite", StringComparison.OrdinalIgnoreCase) ||
                           content.Contains("Microsoft.Data.Sqlite", StringComparison.OrdinalIgnoreCase),
                "Sqlite");

            if (referencingProjects.Count > 0)
            {
                var sqliteNode = new WorkspaceArchitectureNodeDto
                {
                    Id = "sqlite",
                    Label = "SQLite",
                    Sub = "Embedded Database",
                    Layer = "Data",
                    ProjectType = "Database",
                    Path = string.Empty,
                    KeyFiles = new List<string>(),
                    Impacted = false,
                    Why = string.Empty,
                };

                nodes.Add(sqliteNode);

                foreach (var proj in referencingProjects)
                {
                    edges.Add(new WorkspaceArchitectureEdgeDto
                    {
                        From = proj.Id,
                        To = sqliteNode.Id,
                        Type = "DatabaseConnection",
                    });
                }
            }
        }

        // MongoDB: connect only to projects that actually reference MongoDB.Driver
        if (analysis.Technologies.Any(t => t.Name.Equals("MongoDB", StringComparison.OrdinalIgnoreCase)))
        {
            var referencingProjects = GetReferencingProjects(
                content => content.Contains("MongoDB.Driver", StringComparison.OrdinalIgnoreCase),
                "Mongo");

            if (referencingProjects.Count > 0)
            {
                var mongoNode = new WorkspaceArchitectureNodeDto
                {
                    Id = "mongodb",
                    Label = "MongoDB",
                    Sub = "Document Database",
                    Layer = "Data",
                    ProjectType = "Database",
                    Path = string.Empty,
                    KeyFiles = new List<string>(),
                    Impacted = false,
                    Why = string.Empty,
                };

                nodes.Add(mongoNode);

                foreach (var proj in referencingProjects)
                {
                    edges.Add(new WorkspaceArchitectureEdgeDto
                    {
                        From = proj.Id,
                        To = mongoNode.Id,
                        Type = "DatabaseConnection",
                    });
                }
            }
        }
    }

    private static void DetectFrontendProject(
        WorkspaceAnalysisDto analysis,
        List<WorkspaceArchitectureNodeDto> nodes,
        List<WorkspaceArchitectureEdgeDto> edges,
        List<string> allFilePaths)
    {
        var packageJsonPaths = allFilePaths
            .Where(f => f.EndsWith("package.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (packageJsonPaths.Count == 0)
        {
            return;
        }

        // Check if any frontend project node already exists
        foreach (var pkgPath in packageJsonPaths)
        {
            var folder = GetProjectDirectory(pkgPath);
            var folderName = string.IsNullOrWhiteSpace(folder) ? "Frontend" : Path.GetFileName(folder);

            if (nodes.Any(n => n.Label.Equals(folderName, StringComparison.OrdinalIgnoreCase) || n.Id.Equals(folderName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var isReact = analysis.Technologies.Any(t => t.Name.Equals("React", StringComparison.OrdinalIgnoreCase));
            var isVue = analysis.Technologies.Any(t => t.Name.Equals("Vue", StringComparison.OrdinalIgnoreCase));
            var sub = isReact ? "React SPA" : (isVue ? "Vue SPA" : "Frontend SPA");

            var frontendKeyFiles = allFilePaths
                .Where(f => (string.IsNullOrWhiteSpace(folder) || f.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) &&
                            (f.EndsWith("App.tsx", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith("App.vue", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith("main.tsx", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith("main.ts", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith("package.json", StringComparison.OrdinalIgnoreCase)))
                .Take(4)
                .ToList();

            var webNode = new WorkspaceArchitectureNodeDto
            {
                Id = folderName,
                Label = folderName,
                Sub = sub,
                Layer = "Presentation",
                ProjectType = "Frontend",
                Path = folder,
                KeyFiles = frontendKeyFiles,
                Impacted = false,
                Why = string.Empty,
            };

            nodes.Insert(0, webNode);
            // Note: HttpApi edge is NOT inferred between frontend and Web/API projects
            // unless an explicit client/contract relationship is verified.
        }
    }

    private static List<string> SelectDeterministicKeyFiles(
        WorkspaceProjectDto project,
        List<string> projectFiles,
        List<WorkspaceEndpointDto> endpoints)
    {
        var keyFiles = new List<string>();

        // 1. Composition Root / Entry points
        var entryPoints = projectFiles
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("Startup.cs", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("App.tsx", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("main.tsx", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        keyFiles.AddRange(entryPoints);

        // 2. DI / Registration / DbContext
        var diFiles = projectFiles
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.Equals("DependencyInjection.cs", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("ServiceCollectionExtensions.cs", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("DbContext.cs", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var f in diFiles)
        {
            if (!keyFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
            {
                keyFiles.Add(f);
            }
        }

        // 3. Controllers from endpoints or project files
        var controllerFiles = projectFiles
            .Where(f => Path.GetFileName(f).EndsWith("Controller.cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        foreach (var f in controllerFiles)
        {
            if (!keyFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
            {
                keyFiles.Add(f);
            }
        }

        // 4. Core Interfaces / Repositories / Handlers / Services
        var coreFiles = projectFiles
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return (name.StartsWith('I') && (name.EndsWith("Repository.cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Service.cs", StringComparison.OrdinalIgnoreCase))) ||
                       name.EndsWith("Repository.cs", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("Handler.cs", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("Service.cs", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("Query.cs", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("Command.cs", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        foreach (var f in coreFiles)
        {
            if (!keyFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
            {
                keyFiles.Add(f);
            }
        }

        // 5. Domain Entities (if still room)
        if (keyFiles.Count < 3)
        {
            var entityFiles = projectFiles
                .Where(f => f.Contains("/Entities/", StringComparison.OrdinalIgnoreCase) ||
                            f.Contains("/Models/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            foreach (var f in entityFiles)
            {
                if (!keyFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                {
                    keyFiles.Add(f);
                }
            }
        }

        // 6. Fallback if empty: any project source files
        if (keyFiles.Count == 0)
        {
            var fallback = projectFiles
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            keyFiles.AddRange(fallback);
        }

        return keyFiles.Take(5).ToList();
    }

    private static string DeriveProjectSubtitle(WorkspaceProjectDto project, bool hasMediatR, bool hasEfCore)
    {
        return project.Layer switch
        {
            "Web" => "ASP.NET Core",
            "Application" => hasMediatR ? "Use cases · MediatR" : "Use cases · Application Services",
            "Domain" => "Entities · Contracts",
            "Infrastructure" => hasEfCore ? "EF Core · Persistence" : "Infrastructure Services",
            "Tests" => "xUnit · Unit & Integration Tests",
            "Presentation" => "Frontend Application",
            _ => $"{project.ProjectType} Project",
        };
    }

    private static string GetProjectDirectory(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return string.Empty;
        }

        var normalized = projectPath.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash >= 0 ? normalized[..lastSlash] : string.Empty;
    }

    private static void CollectFilePaths(List<WorkspaceFileNodeDto> nodes, List<string> accumulator)
    {
        foreach (var node in nodes)
        {
            if (node.Type.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                accumulator.Add(node.Path.Replace('\\', '/'));
            }

            if (node.Children is not null && node.Children.Count > 0)
            {
                CollectFilePaths(node.Children, accumulator);
            }
        }
    }
}
