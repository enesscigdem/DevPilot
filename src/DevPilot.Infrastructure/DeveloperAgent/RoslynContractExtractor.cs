using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DevPilot.Infrastructure.DeveloperAgent;

public static class RoslynContractExtractor
{
    public static (bool IsValid, IReadOnlyList<string> Errors) ValidateSyntax(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return (false, new[] { "Code content is empty." });
        }

        var tree = CSharpSyntaxTree.ParseText(code);
        var diagnostics = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage()} at line {d.Location.GetLineSpan().StartLinePosition.Line + 1}")
            .ToList();

        return (diagnostics.Count == 0, diagnostics);
    }

    public static string ExtractPublicContracts(string filePath, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();

        var sb = new StringBuilder();
        var fileName = Path.GetFileName(filePath);
        sb.AppendLine($"// File: {filePath}");

        var nsDecl = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (nsDecl != null)
        {
            sb.AppendLine($"namespace {nsDecl.Name};");
            sb.AppendLine();
        }

        var usings = root.Usings.Select(u => u.ToFullString().Trim()).Take(10).ToList();
        if (usings.Count > 0)
        {
            foreach (var u in usings)
            {
                sb.AppendLine(u);
            }
            sb.AppendLine();
        }

        var typeDeclarations = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>();
        foreach (var typeDecl in typeDeclarations)
        {
            var modifiers = typeDecl.Modifiers.ToFullString().Trim();
            if (modifiers.Contains("private") || modifiers.Contains("protected"))
                continue;

            if (typeDecl is RecordDeclarationSyntax recordDecl)
            {
                var paramList = recordDecl.ParameterList?.ToFullString().Trim() ?? "()";
                var baseList = recordDecl.BaseList != null ? " " + recordDecl.BaseList.ToFullString().Trim() : "";
                sb.AppendLine($"{modifiers} record {recordDecl.Identifier.Text}{paramList}{baseList};");
                continue;
            }

            var typeKind = typeDecl switch
            {
                InterfaceDeclarationSyntax => "interface",
                ClassDeclarationSyntax => "class",
                StructDeclarationSyntax => "struct",
                EnumDeclarationSyntax => "enum",
                _ => "class"
            };

            var baseListStr = typeDecl.BaseList != null ? " " + typeDecl.BaseList.ToFullString().Trim() : "";
            sb.AppendLine($"{modifiers} {typeKind} {typeDecl.Identifier.Text}{baseListStr}");
            sb.AppendLine("{");

            // Extract constructors
            var constructors = typeDecl.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
                .Where(c => c.Modifiers.Any(SyntaxKind.PublicKeyword));
            foreach (var ctor in constructors)
            {
                var ctorParams = ctor.ParameterList.ToFullString().Trim();
                sb.AppendLine($"    public {ctor.Identifier.Text}{ctorParams};");
            }

            // Extract public methods
            var methods = typeDecl.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) || typeDecl is InterfaceDeclarationSyntax);
            foreach (var method in methods)
            {
                var retType = method.ReturnType.ToFullString().Trim();
                var methParams = method.ParameterList.ToFullString().Trim();
                var methMod = method.Modifiers.ToFullString().Trim();
                var modPrefix = !string.IsNullOrEmpty(methMod) ? methMod + " " : "";
                sb.AppendLine($"    {modPrefix}{retType} {method.Identifier.Text}{methParams};");
            }

            // Extract public properties
            var properties = typeDecl.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Where(p => p.Modifiers.Any(SyntaxKind.PublicKeyword) || typeDecl is InterfaceDeclarationSyntax);
            foreach (var prop in properties)
            {
                var propType = prop.Type.ToFullString().Trim();
                sb.AppendLine($"    public {propType} {prop.Identifier.Text} {{ get; set; }}");
            }

            // Extract enum members
            if (typeDecl is EnumDeclarationSyntax enumDecl)
            {
                foreach (var member in enumDecl.Members)
                {
                    sb.AppendLine($"    {member.Identifier.Text},");
                }
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    public static (bool IsValid, string? ErrorMessage) ValidateSemanticContractConsistency(
        string consumerFilePath,
        string consumerCode,
        IReadOnlyDictionary<string, string> lockedContracts)
    {
        if (string.IsNullOrWhiteSpace(consumerCode) || lockedContracts == null || lockedContracts.Count == 0)
        {
            return (true, null);
        }

        var tree = CSharpSyntaxTree.ParseText(consumerCode);
        var root = tree.GetCompilationUnitRoot();

        // 1. Check object creation expressions against locked record/class constructors
        var objectCreations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().ToList();
        foreach (var creation in objectCreations)
        {
            var typeName = creation.Type.ToString();
            var shortTypeName = typeName.Contains('.') ? typeName.Split('.').Last() : typeName;

            foreach (var (contractFile, contractText) in lockedContracts)
            {
                var lines = contractText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if ((trimmed.Contains($"record {shortTypeName}(") || trimmed.Contains($"public {shortTypeName}(")) && !trimmed.StartsWith("//"))
                    {
                        var openParen = trimmed.IndexOf('(');
                        var closeParen = trimmed.IndexOf(')', openParen >= 0 ? openParen : 0);
                        if (openParen >= 0 && closeParen > openParen)
                        {
                            var paramsPart = trimmed.Substring(openParen + 1, closeParen - openParen - 1).Trim();
                            var expectedArgCount = string.IsNullOrEmpty(paramsPart)
                                ? 0
                                : paramsPart.Split(',').Length;

                            var actualArgCount = creation.ArgumentList?.Arguments.Count ?? 0;
                            if (expectedArgCount != actualArgCount)
                            {
                                return (false,
                                    $"Semantic contract violation in '{consumerFilePath}': Constructor call 'new {shortTypeName}(...)' has {actualArgCount} argument(s), but locked upstream contract in '{contractFile}' expects {expectedArgCount} parameter(s): '{trimmed}'.");
                            }
                        }
                    }
                }
            }
        }

        // 2. Check method invocations on locked interfaces/classes
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;
                var argCount = invocation.ArgumentList.Arguments.Count;

                foreach (var (contractFile, contractText) in lockedContracts)
                {
                    var lines = contractText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Contains($" {methodName}(") && !trimmed.StartsWith("//"))
                        {
                            var openParen = trimmed.IndexOf('(');
                            var closeParen = trimmed.IndexOf(')', openParen >= 0 ? openParen : 0);
                            if (openParen >= 0 && closeParen > openParen)
                            {
                                var paramsPart = trimmed.Substring(openParen + 1, closeParen - openParen - 1).Trim();
                                var expectedArgCount = string.IsNullOrEmpty(paramsPart) ? 0 : paramsPart.Split(',').Length;

                                if (expectedArgCount != argCount)
                                {
                                    return (false,
                                        $"Semantic contract violation in '{consumerFilePath}': Method invocation '{methodName}(...)' has {argCount} argument(s), but locked contract in '{contractFile}' defines '{trimmed}' with {expectedArgCount} parameter(s).");
                                }
                            }
                        }
                        else if (methodName.EndsWith("Async") && trimmed.Contains($" {methodName[..^5]}(") && !trimmed.StartsWith("//"))
                        {
                            var altName = methodName[..^5];
                            return (false,
                                $"Semantic contract drift in '{consumerFilePath}': Invoking '{methodName}', but locked contract in '{contractFile}' defines '{altName}': '{trimmed}'.");
                        }
                        else if (!methodName.EndsWith("Async") && trimmed.Contains($" {methodName}Async(") && !trimmed.StartsWith("//"))
                        {
                            var altName = methodName + "Async";
                            return (false,
                                $"Semantic contract drift in '{consumerFilePath}': Invoking '{methodName}', but locked contract in '{contractFile}' defines '{altName}': '{trimmed}'.");
                        }
                    }
                }
            }
        }

        return (true, null);
    }

    public static (bool IsValid, string? ErrorMessage) ValidateProjectArchitecturalDependencies(
        string targetFilePath,
        string code,
        IReadOnlyList<DevPilot.Application.DeveloperAgent.Models.DiscoveredProjectNode> projectGraph,
        IReadOnlyDictionary<string, string>? lockedContracts)
    {
        if (string.IsNullOrWhiteSpace(code) || projectGraph == null || projectGraph.Count == 0)
        {
            return (true, null);
        }

        var normalizedPath = targetFilePath.Replace('\\', '/').TrimStart('/');
        var targetProject = projectGraph.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.ProjectDirectory) &&
            p.ProjectDirectory != "." &&
            normalizedPath.StartsWith(p.ProjectDirectory.TrimStart('/'), StringComparison.OrdinalIgnoreCase));

        if (targetProject == null)
        {
            var segments = normalizedPath.Split('/');
            if (segments.Length > 1)
            {
                var candidateDir = $"{segments[0]}/{segments[1]}";
                targetProject = projectGraph.FirstOrDefault(p =>
                    p.ProjectDirectory.TrimStart('/').Equals(candidateDir, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (targetProject == null && projectGraph.Count == 1)
        {
            targetProject = projectGraph[0];
        }

        if (targetProject == null)
        {
            return (true, null);
        }

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();

        // 1. Resolve reachable projects (target project + transitive ProjectReferences)
        var reachableProjects = new HashSet<DevPilot.Application.DeveloperAgent.Models.DiscoveredProjectNode>();
        var queue = new Queue<DevPilot.Application.DeveloperAgent.Models.DiscoveredProjectNode>();
        queue.Enqueue(targetProject);
        reachableProjects.Add(targetProject);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var projRef in current.ProjectReferences)
            {
                var normRef = projRef.Replace('\\', '/').TrimStart('/');
                var referencedProjNode = projectGraph.FirstOrDefault(p =>
                    p.ProjectPath.Replace('\\', '/').TrimStart('/').Equals(normRef, StringComparison.OrdinalIgnoreCase) ||
                    p.ProjectDirectory.Replace('\\', '/').TrimStart('/').Equals(normRef, StringComparison.OrdinalIgnoreCase) ||
                    p.ProjectName.Equals(normRef, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(p.ProjectPath) && Path.GetFileName(p.ProjectPath).Equals(Path.GetFileName(normRef), StringComparison.OrdinalIgnoreCase)));

                if (referencedProjNode != null && reachableProjects.Add(referencedProjNode))
                {
                    queue.Enqueue(referencedProjNode);
                }
            }
        }

        // 2. Allowed namespaces base set: standard BCL / framework namespaces
        var allowedNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System",
            "Microsoft.Extensions",
            "Microsoft.AspNetCore",
            "Microsoft.CSharp",
            "Microsoft.CodeAnalysis"
        };

        // 3. Populate namespaces from reachable projects, their packages, and test frameworks
        foreach (var proj in reachableProjects)
        {
            if (!string.IsNullOrWhiteSpace(proj.ProjectName))
            {
                allowedNamespaces.Add(proj.ProjectName);
            }

            foreach (var projRef in proj.ProjectReferences)
            {
                var refName = Path.GetFileNameWithoutExtension(projRef);
                if (!string.IsNullOrWhiteSpace(refName))
                {
                    allowedNamespaces.Add(refName);
                }
            }

            if (proj.IsTestProject)
            {
                allowedNamespaces.Add("Xunit");
                allowedNamespaces.Add("FluentAssertions");
                allowedNamespaces.Add("Moq");
                allowedNamespaces.Add("NSubstitute");
                allowedNamespaces.Add("Bogus");
            }

            foreach (var pkg in proj.PackageReferences)
            {
                if (string.IsNullOrWhiteSpace(pkg)) continue;
                allowedNamespaces.Add(pkg);
                var parts = pkg.Split('.');
                for (int i = 1; i <= parts.Length; i++)
                {
                    allowedNamespaces.Add(string.Join('.', parts.Take(i)));
                }
            }
        }

        // 4. Populate namespaces from valid locked contracts / overlay
        if (lockedContracts != null)
        {
            foreach (var (_, contractCode) in lockedContracts)
            {
                if (string.IsNullOrWhiteSpace(contractCode)) continue;
                var cTree = CSharpSyntaxTree.ParseText(contractCode);
                foreach (var nsDecl in cTree.GetCompilationUnitRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
                {
                    var nsText = nsDecl.Name.ToString().Trim();
                    if (!string.IsNullOrEmpty(nsText))
                    {
                        allowedNamespaces.Add(nsText);
                    }
                }
            }
        }

        // 5. Validate using directives against reachable allowed namespaces
        foreach (var usingDirective in root.Usings)
        {
            var ns = usingDirective.Name?.ToString().Trim();
            if (string.IsNullOrWhiteSpace(ns)) continue;

            bool isAllowed = allowedNamespaces.Any(allowed =>
                ns.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                ns.StartsWith(allowed + ".", StringComparison.OrdinalIgnoreCase));

            if (!isAllowed)
            {
                return (false,
                    $"Unsupported architectural namespace '{ns}' detected in '{targetFilePath}'. The target project '{targetProject.ProjectName}' does not reference this package or namespace. Follow the existing codebase patterns.");
            }
        }

        // Validate base types, interfaces, and type usages against hallucinated third-party frameworks
        var typeNames = root.DescendantNodes().OfType<TypeSyntax>().Select(t => t.ToString().Trim()).ToList();
        foreach (var typeName in typeNames)
        {
            var shortName = typeName.Contains('<') ? typeName.Substring(0, typeName.IndexOf('<')).Trim() : typeName;
            if (shortName.Contains('.')) shortName = shortName.Split('.').Last();

            if (shortName.Equals("IRequest", StringComparison.OrdinalIgnoreCase) ||
                shortName.Equals("IRequestHandler", StringComparison.OrdinalIgnoreCase) ||
                shortName.Equals("INotification", StringComparison.OrdinalIgnoreCase) ||
                shortName.Equals("INotificationHandler", StringComparison.OrdinalIgnoreCase))
            {
                if (!allowedNamespaces.Contains("MediatR"))
                {
                    return (false,
                        $"Unsupported architectural interface '{shortName}' detected in '{targetFilePath}'. The target project '{targetProject.ProjectName}' does not use MediatR. Implement standard DevPilot queries/handlers without MediatR.");
                }
            }

            if (shortName.Equals("IApplicationDbContext", StringComparison.OrdinalIgnoreCase) ||
                shortName.Equals("DbContext", StringComparison.OrdinalIgnoreCase))
            {
                if (targetProject.ProjectName.Contains("Application", StringComparison.OrdinalIgnoreCase) &&
                    !allowedNamespaces.Any(a => a.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)))
                {
                    return (false,
                        $"Unsupported database abstraction '{shortName}' detected in '{targetFilePath}'. '{targetProject.ProjectName}' relies on domain ports and repositories rather than direct DbContext access.");
                }
            }
        }

        return (true, null);
    }

    public static (bool IsValid, string? ErrorMessage, string? PatternFileName) ValidateProducerAgainstRepositoryPattern(
        string targetFilePath,
        string code,
        string workspacePath,
        IReadOnlyList<DevPilot.Application.DeveloperAgent.Models.DiscoveredProjectNode>? projectGraph = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return (true, null, null);
        }

        var role = RoslynPatternHelper.ClassifyRole(targetFilePath);
        var patternFacts = RoslynPatternHelper.DiscoverNearestPatternFacts(workspacePath, targetFilePath);

        var normalizedPath = targetFilePath.Replace('\\', '/').TrimStart('/');
        var targetProject = projectGraph?.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.ProjectDirectory) &&
            p.ProjectDirectory != "." &&
            normalizedPath.StartsWith(p.ProjectDirectory.TrimStart('/'), StringComparison.OrdinalIgnoreCase));

        var projectHasMediatR = targetProject?.PackageReferences.Contains("MediatR") == true ||
                                projectGraph?.Any(p => p.PackageReferences.Contains("MediatR")) == true;

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();

        // 1. Validate Handlers
        if (string.Equals(role, "Handler", StringComparison.OrdinalIgnoreCase) ||
            targetFilePath.EndsWith("Handler.cs", StringComparison.OrdinalIgnoreCase))
        {
            // Expected handler method name is derived from nearest repository pattern or project capability
            var expectedMethod = patternFacts?.ExpectedHandlerMethodName ?? (projectHasMediatR ? "Handle" : "HandleAsync");

            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) || m.Parent is InterfaceDeclarationSyntax)
                .ToList();

            foreach (var method in methods)
            {
                var retType = method.ReturnType.ToString().Trim();
                var isAsyncReturn = retType.StartsWith("Task", StringComparison.OrdinalIgnoreCase) ||
                                    retType.StartsWith("ValueTask", StringComparison.OrdinalIgnoreCase);

                if (isAsyncReturn && !string.IsNullOrEmpty(expectedMethod))
                {
                    var methodName = method.Identifier.Text;
                    if (string.Equals(expectedMethod, "HandleAsync", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(methodName, "Handle", StringComparison.OrdinalIgnoreCase) &&
                        !projectHasMediatR)
                    {
                        return (false,
                            $"Repository architectural pattern violation in '{targetFilePath}': Handler method is named 'Handle', but the nearest repository pattern from '{patternFacts?.PatternFilePath ?? "the repository"}' requires 'HandleAsync'. Update method and interface to 'HandleAsync'.",
                            patternFacts?.PatternFilePath);
                    }
                    else if (string.Equals(expectedMethod, "Handle", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(methodName, "HandleAsync", StringComparison.OrdinalIgnoreCase) &&
                             (patternFacts?.UsesMediatR == true || projectHasMediatR))
                    {
                        return (false,
                            $"Repository architectural pattern violation in '{targetFilePath}': Handler method is named 'HandleAsync', but the nearest MediatR handler pattern from '{patternFacts?.PatternFilePath ?? "the repository"}' requires 'Handle'. Update method to 'Handle'.",
                            patternFacts?.PatternFilePath);
                    }
                }
            }

            // Ensure class implementing handler interface implements matching method
            var classDecls = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(c => c.Identifier.Text.EndsWith("Handler", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var classDecl in classDecls)
            {
                var classMethods = classDecl.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword))
                    .ToList();

                var hasExpectedMethod = classMethods.Any(m => m.Identifier.Text.Equals(expectedMethod, StringComparison.OrdinalIgnoreCase));
                var hasOtherMethod = string.Equals(expectedMethod, "HandleAsync", StringComparison.OrdinalIgnoreCase)
                    ? classMethods.Any(m => m.Identifier.Text.Equals("Handle", StringComparison.OrdinalIgnoreCase))
                    : classMethods.Any(m => m.Identifier.Text.Equals("HandleAsync", StringComparison.OrdinalIgnoreCase));

                if (!hasExpectedMethod && hasOtherMethod && !projectHasMediatR)
                {
                    return (false,
                        $"Repository architectural pattern violation in '{targetFilePath}': Class '{classDecl.Identifier.Text}' does not implement expected method '{expectedMethod}' required by pattern from '{patternFacts?.PatternFilePath ?? "the repository"}'.",
                        patternFacts?.PatternFilePath);
                }
            }
        }

        // 2. Validate Requests (Queries / Commands)
        if (string.Equals(role, "Request", StringComparison.OrdinalIgnoreCase))
        {
            var typeDecls = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().ToList();
            foreach (var typeDecl in typeDecls)
            {
                if (typeDecl.BaseList != null)
                {
                    var baseTypes = typeDecl.BaseList.Types.Select(t => t.Type.ToString()).ToList();
                    var usesMediatRRequest = baseTypes.Any(bt => bt.StartsWith("IRequest", StringComparison.OrdinalIgnoreCase) ||
                                                                  bt.StartsWith("ICommand", StringComparison.OrdinalIgnoreCase) ||
                                                                  bt.StartsWith("IQuery", StringComparison.OrdinalIgnoreCase));

                    if (usesMediatRRequest && !projectHasMediatR && patternFacts?.UsesMediatR != true)
                    {
                        return (false,
                            $"Repository architectural pattern violation in '{targetFilePath}': Record '{typeDecl.Identifier.Text}' implements 'IRequest', but the target project does not reference MediatR. Follow the plain record pattern from '{patternFacts?.PatternFilePath ?? "the repository"}'.",
                            patternFacts?.PatternFilePath);
                    }
                }
            }
        }

        return (true, null, patternFacts?.PatternFilePath);
    }

    private static readonly HashSet<string> StandardBclTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "string", "bool", "long", "double", "float", "byte", "char", "object", "void", "decimal", "short", "uint", "ulong", "ushort", "sbyte",
        "Guid", "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan", "CancellationToken", "Task", "ValueTask", "Action", "Func", "Predicate",
        "IEnumerable", "IAsyncEnumerable", "IReadOnlyList", "IList", "List", "IReadOnlyCollection", "ICollection", "Dictionary", "IDictionary", "IReadOnlyDictionary",
        "HashSet", "ISet", "Nullable", "Exception", "ArgumentException", "ArgumentNullException", "InvalidOperationException", "OperationCanceledException",
        "Uri", "Stream", "MemoryStream", "StringBuilder", "StringComparison", "StringSplitOptions", "Math", "Convert", "Type", "Attribute",
        "ILogger", "ILoggerFactory", "IOptions", "IOptionsMonitor", "IOptionsSnapshot", "IServiceCollection", "IServiceProvider", "IConfiguration",
        "IActionResult", "ActionResult", "ControllerBase", "Controller", "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch",
        "FromRoute", "FromBody", "FromQuery", "FromHeader", "FromServices", "Ok", "NotFound", "BadRequest", "Conflict", "NoContent", "Forbid", "Unauthorized",
        "JsonResult", "StatusCode", "Problem", "CancellationTokenSource", "Stopwatch", "Fact", "Theory", "InlineData", "MemberData", "ClassData",
        "Assert", "Should", "FluentActions", "IRequest", "IRequestHandler", "INotification", "INotificationHandler", "ISender", "IMediator",
        "DbContext", "DbSet", "Entity", "ValueObject", "IEntityTypeConfiguration", "EntityTypeBuilder", "ModelBuilder", "FluentValidation",
        "AbstractValidator", "RuleFor", "IValidator", "ValidationResult", "CancellationTokenRegistration", "KeyValuePair", "IDisposable", "IAsyncDisposable"
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime ScannedAt, HashSet<string> Symbols)> WorkspaceSymbolCache = new();

    public static HashSet<string> GetWorkspaceDeclaredSymbols(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var key = Path.GetFullPath(workspacePath);
        if (WorkspaceSymbolCache.TryGetValue(key, out var cached) && (DateTime.UtcNow - cached.ScannedAt).TotalSeconds < 30)
        {
            return cached.Symbols;
        }

        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var files = Directory.GetFiles(workspacePath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                            !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                            !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                var content = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(content);
                var root = tree.GetCompilationUnitRoot();
                var typeDecls = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>();
                foreach (var td in typeDecls)
                {
                    symbols.Add(td.Identifier.Text);
                }
                var delegateDecls = root.DescendantNodes().OfType<DelegateDeclarationSyntax>();
                foreach (var dd in delegateDecls)
                {
                    symbols.Add(dd.Identifier.Text);
                }
            }
        }
        catch
        {
            // best-effort
        }

        WorkspaceSymbolCache[key] = (DateTime.UtcNow, symbols);
        return symbols;
    }

    public static (bool IsValid, string? ErrorMessage) ValidateSymbolResolution(
        string targetFilePath,
        string code,
        string workspacePath,
        IReadOnlyDictionary<string, string>? lockedContracts = null,
        IReadOnlyList<DevPilot.Application.DeveloperAgent.Models.DiscoveredProjectNode>? projectGraph = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return (true, null);

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();

        var localTypes = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Select(t => t.Identifier.Text)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lockedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (lockedContracts != null)
        {
            foreach (var (_, contractCode) in lockedContracts)
            {
                var cTree = CSharpSyntaxTree.ParseText(contractCode);
                foreach (var td in cTree.GetCompilationUnitRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    lockedSymbols.Add(td.Identifier.Text);
                }
            }
        }

        var workspaceSymbols = GetWorkspaceDeclaredSymbols(workspacePath);

        // Find all referenced type syntax nodes
        var referencedTypeNodes = new List<string>();

        // Fields
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            referencedTypeNodes.Add(field.Declaration.Type.ToString());
        }

        // Properties
        foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            referencedTypeNodes.Add(prop.Type.ToString());
        }

        // Constructor parameters
        foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            foreach (var param in ctor.ParameterList.Parameters)
            {
                if (param.Type != null) referencedTypeNodes.Add(param.Type.ToString());
            }
        }

        // Method return types and parameters
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            referencedTypeNodes.Add(method.ReturnType.ToString());
            foreach (var param in method.ParameterList.Parameters)
            {
                if (param.Type != null) referencedTypeNodes.Add(param.Type.ToString());
            }
        }

        // Base types / interfaces
        foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (typeDecl.BaseList != null)
            {
                foreach (var bt in typeDecl.BaseList.Types)
                {
                    referencedTypeNodes.Add(bt.Type.ToString());
                }
            }
        }

        // Object creations
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            referencedTypeNodes.Add(creation.Type.ToString());
        }

        foreach (var rawType in referencedTypeNodes)
        {
            var cleanType = rawType.Trim();
            // Split generic arguments (e.g. Task<IRepositoryWorkspaceRepository> -> Task, IRepositoryWorkspaceRepository)
            var parts = cleanType.Split(new[] { '<', '>', ',', '?', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p));

            foreach (var part in parts)
            {
                var shortName = part.Contains('.') ? part.Split('.').Last() : part;
                if (string.IsNullOrWhiteSpace(shortName)) continue;

                // Check if symbol looks like an interface or custom type
                bool isCustomCandidate = (shortName.Length > 2 && shortName.StartsWith("I", StringComparison.Ordinal) && char.IsUpper(shortName[1])) ||
                                         (!StandardBclTypes.Contains(shortName) && char.IsUpper(shortName[0]) && shortName.EndsWith("Repository", StringComparison.OrdinalIgnoreCase));

                if (isCustomCandidate &&
                    !StandardBclTypes.Contains(shortName) &&
                    !localTypes.Contains(shortName) &&
                    !lockedSymbols.Contains(shortName) &&
                    !workspaceSymbols.Contains(shortName))
                {
                    var candidatePorts = workspaceSymbols
                        .Where(s => s.StartsWith("I", StringComparison.Ordinal) && (s.EndsWith("Query", StringComparison.OrdinalIgnoreCase) || s.EndsWith("Repository", StringComparison.OrdinalIgnoreCase) || s.EndsWith("Reader", StringComparison.OrdinalIgnoreCase) || s.EndsWith("Handler", StringComparison.OrdinalIgnoreCase)))
                        .Take(6)
                        .ToList();

                    var candidateStr = candidatePorts.Count > 0 ? string.Join(", ", candidatePorts) : "none found";
                    return (false,
                        $"Unresolved symbol '{shortName}' detected in '{targetFilePath}'. The type or interface '{shortName}' does not exist in the repository or approved contracts. Available ports/abstractions in codebase: {candidateStr}.");
                }
            }
        }

        return (true, null);
    }

    public static List<(string Namespace, string TypeName)> ExtractDeclaredTypesWithNamespace(string code)
    {
        var list = new List<(string Namespace, string TypeName)>();
        if (string.IsNullOrWhiteSpace(code)) return list;
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();

        foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            var typeName = typeDecl.Identifier.Text;
            var ns = typeDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString().Trim() ?? string.Empty;
            list.Add((ns, typeName));
        }
        return list;
    }

    public static (bool IsValid, string? ErrorMessage) ValidateNoDuplicateTypeDeclarations(
        string targetFilePath,
        string targetCode,
        IReadOnlyDictionary<string, DevPilot.Application.DeveloperAgent.Models.FileEditSpec>? completedEdits,
        IReadOnlyDictionary<string, string>? lockedContracts = null)
    {
        if (string.IsNullOrWhiteSpace(targetCode)) return (true, null);

        var currentTypes = ExtractDeclaredTypesWithNamespace(targetCode);
        if (currentTypes.Count == 0) return (true, null);

        var currentTypeSet = currentTypes.ToHashSet();

        if (completedEdits != null)
        {
            foreach (var (otherPath, editSpec) in completedEdits)
            {
                if (string.Equals(otherPath, targetFilePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var otherCode = editSpec.NewContent;
                if (string.IsNullOrWhiteSpace(otherCode)) continue;

                var otherTypes = ExtractDeclaredTypesWithNamespace(otherCode);
                foreach (var other in otherTypes)
                {
                    var matched = currentTypes.FirstOrDefault(cur =>
                        string.Equals(cur.TypeName, other.TypeName, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrEmpty(cur.Namespace) || string.IsNullOrEmpty(other.Namespace) || string.Equals(cur.Namespace, other.Namespace, StringComparison.OrdinalIgnoreCase)));

                    if (!string.IsNullOrEmpty(matched.TypeName))
                    {
                        var fullTypeName = string.IsNullOrEmpty(other.Namespace) ? other.TypeName : $"{other.Namespace}.{other.TypeName}";
                        return (false,
                            $"Duplicate type declaration detected: Type '{fullTypeName}' is declared in multiple generated files: '{Path.GetFileName(otherPath)}' and '{Path.GetFileName(targetFilePath)}'. Remove the duplicate type definition.");
                    }
                }
            }
        }

        if (lockedContracts != null)
        {
            foreach (var (otherPath, contractContent) in lockedContracts)
            {
                if (string.Equals(otherPath, targetFilePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(contractContent)) continue;

                var otherTypes = ExtractDeclaredTypesWithNamespace(contractContent);
                foreach (var other in otherTypes)
                {
                    var matched = currentTypes.FirstOrDefault(cur =>
                        string.Equals(cur.TypeName, other.TypeName, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrEmpty(cur.Namespace) || string.IsNullOrEmpty(other.Namespace) || string.Equals(cur.Namespace, other.Namespace, StringComparison.OrdinalIgnoreCase)));

                    if (!string.IsNullOrEmpty(matched.TypeName))
                    {
                        var fullTypeName = string.IsNullOrEmpty(other.Namespace) ? other.TypeName : $"{other.Namespace}.{other.TypeName}";
                        return (false,
                            $"Duplicate type declaration detected: Type '{fullTypeName}' is declared in multiple generated files: '{Path.GetFileName(otherPath)}' and '{Path.GetFileName(targetFilePath)}'. Remove the duplicate type definition.");
                    }
                }
            }
        }

        return (true, null);
    }

    public static string GetAvailablePortDescriptions(string workspacePath, IEnumerable<string> correlatedFiles)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return string.Empty;

        var sb = new StringBuilder();
        try
        {
            var searchRoot = Directory.Exists(Path.Combine(workspacePath, "src")) ? Path.Combine(workspacePath, "src") : workspacePath;

            var portFiles = Directory.GetFiles(searchRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                            !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                            (f.Contains($"{Path.DirectorySeparatorChar}Ports{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                             Path.GetFileName(f).StartsWith("I", StringComparison.Ordinal)))
                .OrderByDescending(f => f.Contains($"{Path.DirectorySeparatorChar}Ports{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => f.Contains("Tasks", StringComparison.OrdinalIgnoreCase) || f.Contains("Workspace", StringComparison.OrdinalIgnoreCase))
                .Take(40);

            foreach (var file in portFiles)
            {
                var content = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(content);
                var root = tree.GetCompilationUnitRoot();

                var ifaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>();
                foreach (var iface in ifaces)
                {
                    var ns = iface.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString().Trim() ?? "DevPilot.Application";
                    sb.AppendLine($"- {iface.Identifier.Text} (Namespace: {ns})");
                    var methods = iface.DescendantNodes().OfType<MethodDeclarationSyntax>().Take(3);
                    foreach (var m in methods)
                    {
                        sb.AppendLine($"    {m.ReturnType} {m.Identifier.Text}{m.ParameterList};");
                    }
                }
            }
        }
        catch
        {
            // best-effort
        }

        return sb.ToString().Trim();
    }
}
