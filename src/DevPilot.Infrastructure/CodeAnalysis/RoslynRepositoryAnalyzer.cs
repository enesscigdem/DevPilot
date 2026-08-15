using System.Text.RegularExpressions;
using System.Xml.Linq;
using DevPilot.Application.CodeAnalysis;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.CodeAnalysis;

internal sealed partial class RoslynRepositoryAnalyzer : IRepositoryAnalyzer
{
    private static readonly SymbolDisplayFormat s_minimalSymbolFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;
    private static readonly SymbolDisplayFormat s_qualifiedNamespaceFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    private static readonly object s_msbuildInitLock = new();
    private static bool s_msbuildRegistered;

    private readonly ILogger<RoslynRepositoryAnalyzer> _logger;

    public RoslynRepositoryAnalyzer(ILogger<RoslynRepositoryAnalyzer> logger)
    {
        _logger = logger;
    }

    public async Task<RepositoryAnalysisResult> AnalyzeAsync(
        RepositoryAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = new RepositoryAnalysisResult();

        try
        {
            EnsureMsBuildRegistered();
        }
        catch (Exception ex)
        {
            result.Error = $"Failed to initialize MSBuild: {ex.Message}";
            return result;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            result.Error = "WorkspacePath is required.";
            return result;
        }

        var workspacePath = Path.GetFullPath(request.WorkspacePath.Trim());

        if (!Directory.Exists(workspacePath))
        {
            result.Error = $"Workspace path does not exist: {workspacePath}";
            return result;
        }

        try
        {
            var solutionFiles = FindFiles(workspacePath, new[] { ".sln", ".slnx" });

            if (solutionFiles.Count > 0)
            {
                foreach (var solutionFile in solutionFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var solutionResult = await AnalyzeSolutionAsync(solutionFile, result.Warnings, cancellationToken)
                        .ConfigureAwait(false);
                    if (solutionResult is not null)
                    {
                        result.Solutions.Add(solutionResult);
                    }
                }
            }
            else
            {
                var projectFiles = FindFiles(workspacePath, new[] { ".csproj" });
                foreach (var projectFile in projectFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var projectResult = await AnalyzeStandaloneProjectAsync(projectFile, result.Warnings, cancellationToken)
                        .ConfigureAwait(false);
                    if (projectResult is not null)
                    {
                        result.StandaloneProjects.Add(projectResult);
                    }
                }
            }

            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Repository analysis failed for workspace {WorkspacePath}", workspacePath);
            result.Error = $"Analysis failed: {ex.Message}";
        }

        return result;
    }

    private static void EnsureMsBuildRegistered()
    {
        if (s_msbuildRegistered)
        {
            return;
        }

        lock (s_msbuildInitLock)
        {
            if (s_msbuildRegistered)
            {
                return;
            }

            try
            {
                if (!MSBuildLocator.IsRegistered)
                {
                    MSBuildLocator.RegisterDefaults();
                }
            }
            catch (InvalidOperationException)
            {
                // Already registered by another component.
            }

            s_msbuildRegistered = true;
        }
    }

    private static List<string> FindFiles(string root, string[] extensions)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> subdirectories;

            try
            {
                subdirectories = Directory.EnumerateDirectories(directory);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (IsExcludedDirectoryName(name))
                {
                    continue;
                }

                pending.Push(subdirectory);
            }

            IEnumerable<string> directoryFiles;

            try
            {
                directoryFiles = Directory.EnumerateFiles(directory);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in directoryFiles)
            {
                if (extensions.Any(extension =>
                    file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                {
                    files.Add(file);
                }
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }
    private async Task<SolutionAnalysisResult?> AnalyzeSolutionAsync(
        string solutionFile,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var solutionResult = new SolutionAnalysisResult
        {
            Name = Path.GetFileNameWithoutExtension(solutionFile),
            Path = solutionFile,
        };

        using var workspace = CreateWorkspace(solutionResult.Warnings);
        Solution? solution = null;

        try
        {
            solution = await workspace.OpenSolutionAsync(solutionFile, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open solution {SolutionPath}", solutionFile);
            solutionResult.Warnings.Add($"Failed to open solution via MSBuildWorkspace: {ex.Message}");
        }

        if (solution is not null)
        {
            foreach (var project in solution.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.Equals(project.Language, LanguageNames.CSharp, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var projectResult = await AnalyzeProjectAsync(project, cancellationToken)
                    .ConfigureAwait(false);
                solutionResult.Projects.Add(projectResult);
            }
        }
        else
        {
            var projectPaths = ParseSolutionProjectPaths(solutionFile);
            foreach (var projectPath in projectPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var projectResult = await AnalyzeStandaloneProjectAsync(projectPath, solutionResult.Warnings, cancellationToken)
                    .ConfigureAwait(false);
                if (projectResult is not null)
                {
                    solutionResult.Projects.Add(projectResult);
                }
            }
        }

        return solutionResult;
    }

    private static List<string> ParseSolutionProjectPaths(string solutionFile)
    {
        var projectPaths = new List<string>();
        var solutionDirectory = Path.GetDirectoryName(solutionFile)!;

        if (solutionFile.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var document = XDocument.Load(solutionFile);
                foreach (var projectElement in document.Descendants())
                {
                    if (!projectElement.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var pathAttribute = projectElement.Attribute("Path")?.Value;
                    if (string.IsNullOrWhiteSpace(pathAttribute) ||
                        !pathAttribute.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var fullPath = Path.GetFullPath(Path.Combine(solutionDirectory, pathAttribute.Replace('\\', Path.DirectorySeparatorChar)));
                    projectPaths.Add(fullPath);
                }
            }
            catch
            {
                // Best-effort fallback: cannot parse solution.
            }

            return projectPaths;
        }

        try
        {
            var lines = File.ReadAllLines(solutionFile);
            foreach (var line in lines)
            {
                var match = SolutionProjectLineRegex().Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var relativePath = match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar);
                if (!relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(Path.Combine(solutionDirectory, relativePath));
                projectPaths.Add(fullPath);
            }
        }
        catch
        {
            // Best-effort fallback: cannot parse solution.
        }

        projectPaths.Sort(StringComparer.OrdinalIgnoreCase);
        return projectPaths;
    }

    [GeneratedRegex(@"Project\([^\)]*\)\s*=\s*""[^""]*""\s*,\s*""([^""]*)""\s*,\s*""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex SolutionProjectLineRegex();

    private async Task<ProjectAnalysisResult?> AnalyzeStandaloneProjectAsync(
        string projectFile,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        using var workspace = CreateWorkspace(warnings);
        Project? project = null;

        try
        {
            project = await workspace.OpenProjectAsync(projectFile, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open project {ProjectPath}", projectFile);
            warnings.Add($"Failed to open project '{projectFile}': {ex.Message}");
        }

        if (project is not null)
        {
            return await AnalyzeProjectAsync(project, cancellationToken).ConfigureAwait(false);
        }

        return await AnalyzeProjectFromSourceAsync(projectFile, warnings, cancellationToken)
            .ConfigureAwait(false);
    }

    private MSBuildWorkspace CreateWorkspace(List<string> warnings)
    {
        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(args =>
        {
            var message = $"Workspace diagnostic ({args.Diagnostic.Kind}): {args.Diagnostic.Message}";
            warnings.Add(message);
            _logger.LogWarning("{Message}", message);
        });

        return workspace;
    }

    private async Task<ProjectAnalysisResult> AnalyzeProjectAsync(
        Project project,
        CancellationToken cancellationToken)
    {
        var result = new ProjectAnalysisResult
        {
            Name = project.Name,
            Path = project.FilePath ?? string.Empty,
        };

        FillProjectMetadata(result);

        foreach (var projectReference in project.ProjectReferences)
        {
            var referencedProject = project.Solution.GetProject(projectReference.ProjectId);
            result.ProjectReferences.Add(new ProjectReferenceInfo
            {
                Name = referencedProject?.Name ?? projectReference.ProjectId.Id.ToString(),
                Path = referencedProject?.FilePath ?? string.Empty,
            });
        }

        Compilation? compilation = null;

        if (project.SupportsCompilation)
        {
            try
            {
                compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Compilation failed for project '{project.Name}': {ex.Message}");
            }
        }
        else
        {
            result.Warnings.Add($"Project '{project.Name}' does not support compilation; syntax-only analysis used.");
        }

        if (compilation is not null)
        {
            var allErrors = compilation.GetDiagnostics(cancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            var topErrors = allErrors.Take(20).ToList();
            result.CompilationSucceeded = allErrors.Count == 0;
            result.CompilationErrors.AddRange(topErrors.Select(FormatDiagnostic));

            if (allErrors.Count > 0)
            {
                result.Warnings.Add($"Project '{project.Name}' has {allErrors.Count} compilation error(s); partial analysis produced.");
            }
        }
        else
        {
            result.Warnings.Add($"Project '{project.Name}' has no usable compilation; syntax-only analysis produced.");
        }

        foreach (var document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = document.FilePath;
            if (string.IsNullOrEmpty(filePath) || IsExcludedFilePath(filePath))
            {
                continue;
            }

            if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SyntaxNode? root;
            try
            {
                root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to parse '{filePath}': {ex.Message}");
                continue;
            }

            if (root is null)
            {
                continue;
            }

            if (IsAutoGeneratedDocument(root))
            {
                continue;
            }

            SemanticModel? semanticModel = null;
            try
            {
                semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Syntax-only fallback.
            }

            AnalyzeTypeDeclarations(root, filePath, semanticModel, result);
        }

        SortResults(result);
        return result;
    }

    private async Task<ProjectAnalysisResult> AnalyzeProjectFromSourceAsync(
        string projectFile,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var result = new ProjectAnalysisResult
        {
            Name = Path.GetFileNameWithoutExtension(projectFile),
            Path = projectFile,
        };

        FillProjectMetadata(result);
        var projectDirectory = Path.GetDirectoryName(projectFile)!;
        var sourceFiles = FindFiles(projectDirectory, new[] { ".cs" });

        foreach (var file in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsExcludedFilePath(file))
            {
                continue;
            }

            SyntaxNode? root;
            try
            {
                var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var tree = CSharpSyntaxTree.ParseText(SourceText.From(text), path: file);
                root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to parse fallback source file '{file}': {ex.Message}");
                continue;
            }

            if (IsAutoGeneratedDocument(root))
            {
                continue;
            }

            AnalyzeTypeDeclarations(root, file, null, result);
        }

        SortResults(result);
        return result;
    }

    private static void AnalyzeTypeDeclarations(
        SyntaxNode root,
        string sourcePath,
        SemanticModel? semanticModel,
        ProjectAnalysisResult projectResult)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case ClassDeclarationSyntax classDeclaration:
                    var classInfo = BuildTypeInfo(classDeclaration, sourcePath, semanticModel);
                    if (classInfo is null)
                    {
                        break;
                    }

                    if (IsController(classDeclaration, semanticModel))
                    {
                        var controller = new ControllerAnalysisResult
                        {
                            Name = classInfo.Name,
                            Namespace = classInfo.Namespace,
                            SourcePath = classInfo.SourcePath,
                            BaseType = classInfo.BaseType,
                            Methods = classInfo.Methods,
                            Constructors = classInfo.Constructors,
                            Properties = classInfo.Properties,
                            Actions = GetControllerActions(classDeclaration, sourcePath, semanticModel),
                        };
                        projectResult.Controllers.Add(controller);
                    }
                    else
                    {
                        projectResult.Classes.Add(classInfo);
                    }
                    break;

                case InterfaceDeclarationSyntax interfaceDeclaration:
                    var interfaceInfo = BuildTypeInfo(interfaceDeclaration, sourcePath, semanticModel);
                    if (interfaceInfo is not null)
                    {
                        projectResult.Interfaces.Add(interfaceInfo);
                    }
                    break;

                case RecordDeclarationSyntax recordDeclaration:
                    var recordInfo = BuildTypeInfo(recordDeclaration, sourcePath, semanticModel);
                    if (recordInfo is not null)
                    {
                        projectResult.Records.Add(recordInfo);
                    }
                    break;

                case EnumDeclarationSyntax enumDeclaration:
                    var enumInfo = BuildEnumInfo(enumDeclaration, sourcePath, semanticModel);
                    if (enumInfo is not null)
                    {
                        projectResult.Enums.Add(enumInfo);
                    }
                    break;
            }
        }
    }

    private static TypeAnalysisResult? BuildTypeInfo(
        TypeDeclarationSyntax declaration,
        string sourcePath,
        SemanticModel? semanticModel)
    {
        INamedTypeSymbol? symbol = null;
        try
        {
            symbol = semanticModel?.GetDeclaredSymbol(declaration);
        }
        catch
        {
            // Continue with syntax-only information.
        }

        var namespaceName = symbol?.ContainingNamespace?.ToDisplayString(s_qualifiedNamespaceFormat)
            ?? GetNamespaceFromSyntax(declaration);
        var typeName = symbol?.Name ?? declaration.Identifier.ValueText;

        var result = new TypeAnalysisResult
        {
            Name = typeName,
            Namespace = namespaceName,
            SourcePath = sourcePath,
            BaseType = GetBaseType(symbol, declaration),
        };

        if (symbol is not null)
        {
            PopulateMembersFromSymbol(result, symbol, sourcePath, namespaceName);
        }
        else
        {
            PopulateMembersFromSyntax(result, declaration, sourcePath, namespaceName);
        }

        return result;
    }

    private static EnumAnalysisResult? BuildEnumInfo(
        EnumDeclarationSyntax declaration,
        string sourcePath,
        SemanticModel? semanticModel)
    {
        INamedTypeSymbol? symbol = null;
        try
        {
            symbol = semanticModel?.GetDeclaredSymbol(declaration);
        }
        catch
        {
            // Continue with syntax-only information.
        }

        var result = new EnumAnalysisResult
        {
            Name = symbol?.Name ?? declaration.Identifier.ValueText,
            Namespace = symbol?.ContainingNamespace?.ToDisplayString(s_qualifiedNamespaceFormat)
                ?? GetNamespaceFromSyntax(declaration),
            SourcePath = sourcePath,
        };

        foreach (var member in declaration.Members.OfType<EnumMemberDeclarationSyntax>())
        {
            result.Values.Add(member.Identifier.ValueText);
        }

        return result;
    }

    private static string? GetBaseType(INamedTypeSymbol? symbol, TypeDeclarationSyntax declaration)
    {
        if (symbol is not null)
        {
            if (symbol.BaseType is not null &&
                symbol.BaseType.SpecialType != SpecialType.System_Object)
            {
                return symbol.BaseType.ToDisplayString(s_minimalSymbolFormat);
            }

            return null;
        }

        return declaration.BaseList?.Types.FirstOrDefault()?.Type.ToString();
    }

    private static void PopulateMembersFromSymbol(
        TypeAnalysisResult result,
        INamedTypeSymbol symbol,
        string sourcePath,
        string namespaceName)
    {
        foreach (var member in symbol.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
            {
                continue;
            }

            if (member.Locations.All(l => !l.IsInSource))
            {
                continue;
            }

            if (!member.Locations.Any(l => l.SourceTree?.FilePath == sourcePath))
            {
                continue;
            }

            switch (member)
            {
                case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                    result.Methods.Add(BuildMethodInfo(method, sourcePath, namespaceName));
                    break;

                case IMethodSymbol method when method.MethodKind == MethodKind.Constructor:
                    result.Constructors.Add(BuildMethodInfo(method, sourcePath, namespaceName));
                    break;

                case IPropertySymbol property when property.DeclaredAccessibility == Accessibility.Public:
                    result.Properties.Add(new PropertyAnalysisResult
                    {
                        Name = property.Name,
                        Namespace = property.ContainingNamespace?.ToDisplayString(s_qualifiedNamespaceFormat) ?? namespaceName,
                        SourcePath = sourcePath,
                        TypeName = property.Type.ToDisplayString(s_minimalSymbolFormat),
                        Accessibility = property.DeclaredAccessibility.ToString().ToLowerInvariant(),
                        IsStatic = property.IsStatic,
                    });
                    break;
            }
        }
    }

    private static MethodAnalysisResult BuildMethodInfo(IMethodSymbol method, string sourcePath, string namespaceName)
    {
        return new MethodAnalysisResult
        {
            Name = method.Name,
            Namespace = method.ContainingNamespace?.ToDisplayString(s_qualifiedNamespaceFormat) ?? namespaceName,
            SourcePath = sourcePath,
            ReturnType = method.MethodKind == MethodKind.Constructor
                ? null
                : method.ReturnType.ToDisplayString(s_minimalSymbolFormat),
            Accessibility = method.DeclaredAccessibility.ToString().ToLowerInvariant(),
            IsStatic = method.IsStatic,
            IsConstructor = method.MethodKind == MethodKind.Constructor,
        };
    }

    private static void PopulateMembersFromSyntax(
        TypeAnalysisResult result,
        TypeDeclarationSyntax declaration,
        string sourcePath,
        string namespaceName)
    {
        var isInterface = declaration is InterfaceDeclarationSyntax;

        foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
        {
            result.Methods.Add(new MethodAnalysisResult
            {
                Name = method.Identifier.ValueText,
                Namespace = namespaceName,
                SourcePath = sourcePath,
                ReturnType = method.ReturnType.ToString(),
                Accessibility = GetAccessibility(method.Modifiers, isInterface),
                IsStatic = method.Modifiers.Any(SyntaxKind.StaticKeyword),
            });
        }

        foreach (var constructor in declaration.Members.OfType<ConstructorDeclarationSyntax>())
        {
            result.Constructors.Add(new MethodAnalysisResult
            {
                Name = constructor.Identifier.ValueText,
                Namespace = namespaceName,
                SourcePath = sourcePath,
                ReturnType = null,
                Accessibility = GetAccessibility(constructor.Modifiers, false),
                IsStatic = constructor.Modifiers.Any(SyntaxKind.StaticKeyword),
                IsConstructor = true,
            });
        }

        foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (GetAccessibility(property.Modifiers, isInterface) == "public")
            {
                result.Properties.Add(new PropertyAnalysisResult
                {
                    Name = property.Identifier.ValueText,
                    Namespace = namespaceName,
                    SourcePath = sourcePath,
                    TypeName = property.Type.ToString(),
                    Accessibility = "public",
                    IsStatic = property.Modifiers.Any(SyntaxKind.StaticKeyword),
                });
            }
        }
    }

    private static string GetAccessibility(SyntaxTokenList modifiers, bool isInterfaceMember)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword))
        {
            return "public";
        }

        if (modifiers.Any(SyntaxKind.PrivateKeyword))
        {
            return "private";
        }

        if (modifiers.Any(SyntaxKind.ProtectedKeyword))
        {
            return "protected";
        }

        if (modifiers.Any(SyntaxKind.InternalKeyword))
        {
            return "internal";
        }

        return isInterfaceMember ? "public" : "private";
    }

    private static string GetNamespaceFromSyntax(SyntaxNode node)
    {
        var ns = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        return ns?.Name.ToString() ?? string.Empty;
    }

    private static bool IsController(TypeDeclarationSyntax declaration, SemanticModel? semanticModel)
    {
        if (semanticModel is not null)
        {
            try
            {
                var symbol = semanticModel.GetDeclaredSymbol(declaration) as INamedTypeSymbol;
                var current = symbol;
                while (current is not null)
                {
                    var name = current.Name;
                    if (name is "Controller" or "ControllerBase")
                    {
                        return true;
                    }

                    var fullName = current.ToDisplayString();
                    if (fullName.Contains("Microsoft.AspNetCore.Mvc.Controller", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    current = current.BaseType;
                }
            }
            catch
            {
                // Fall back to syntax analysis.
            }
        }

        if (declaration.BaseList is not null)
        {
            foreach (var baseType in declaration.BaseList.Types)
            {
                var text = baseType.Type.ToString();
                var simpleName = text.Split('.').LastOrDefault() ?? text;
                if (simpleName is "Controller" or "ControllerBase")
                {
                    return true;
                }
            }
        }

        var className = declaration.Identifier.ValueText;
        if (className.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
        {
            var attributes = declaration.AttributeLists.SelectMany(a => a.Attributes).ToList();
            var attributeNames = attributes
                .Select(a => a.Name.ToString().Split('.').LastOrDefault() ?? a.Name.ToString())
                .ToList();
            if (attributeNames.Contains("ApiController") || attributeNames.Contains("Route"))
            {
                return true;
            }
        }

        return false;
    }

    private static List<ControllerActionAnalysisResult> GetControllerActions(
        TypeDeclarationSyntax controller,
        string sourcePath,
        SemanticModel? semanticModel)
    {
        var actions = new List<ControllerActionAnalysisResult>();
        var controllerName = controller.Identifier.ValueText;
        var controllerRoute = GetControllerRouteTemplate(controller.AttributeLists, semanticModel, controller)
            ?? string.Empty;

        foreach (var method in controller.Members.OfType<MethodDeclarationSyntax>())
        {
            var isPublicAction = method.Modifiers.Any(SyntaxKind.PublicKeyword) &&
                !method.Modifiers.Any(SyntaxKind.StaticKeyword);

            if (semanticModel is not null)
            {
                try
                {
                    var methodSymbol = semanticModel.GetDeclaredSymbol(method) as IMethodSymbol;
                    if (methodSymbol is not null)
                    {
                        isPublicAction = methodSymbol.DeclaredAccessibility == Accessibility.Public &&
                            !methodSymbol.IsStatic &&
                            methodSymbol.MethodKind == MethodKind.Ordinary;
                    }
                }
                catch
                {
                    // Keep syntax-based decision.
                }
            }

            if (!isPublicAction)
            {
                continue;
            }

            var actionName = method.Identifier.ValueText;
            var (httpMethod, actionRoute) = GetActionVerbAndRoute(method, semanticModel);
            var combinedRoute = CombineRoutes(controllerRoute, actionRoute);
            combinedRoute = ExpandRouteTokens(combinedRoute, controllerName, actionName);

            actions.Add(new ControllerActionAnalysisResult
            {
                Name = actionName,
                HttpMethod = httpMethod,
                RouteTemplate = string.IsNullOrWhiteSpace(combinedRoute) ? null : combinedRoute,
                SourcePath = sourcePath,
            });
        }

        return actions;
    }

    private static string? GetControllerRouteTemplate(
        SyntaxList<AttributeListSyntax> attributeLists,
        SemanticModel? semanticModel,
        TypeDeclarationSyntax controller)
    {
        foreach (var attribute in attributeLists.SelectMany(a => a.Attributes))
        {
            var name = attribute.Name.ToString().Split('.').LastOrDefault() ?? attribute.Name.ToString();
            if (name is "Route" or "RouteAttribute")
            {
                return GetTemplateFromAttributeSyntax(attribute);
            }
        }

        if (semanticModel is not null)
        {
            try
            {
                var symbol = semanticModel.GetDeclaredSymbol(controller) as INamedTypeSymbol;
                if (symbol is not null)
                {
                    foreach (var attributeData in symbol.GetAttributes())
                    {
                        var attrName = attributeData.AttributeClass?.Name ?? string.Empty;
                        if (attrName.Contains("RouteAttribute", StringComparison.Ordinal) ||
                            attrName.Equals("Route", StringComparison.Ordinal))
                        {
                            var template = GetTemplateFromAttributeData(attributeData);
                            if (!string.IsNullOrWhiteSpace(template))
                            {
                                return template;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Syntax fallback already used.
            }
        }

        return null;
    }

    private static (string? HttpMethod, string? Route) GetActionVerbAndRoute(
        MethodDeclarationSyntax method,
        SemanticModel? semanticModel)
    {
        string? httpMethod = null;
        string? route = null;

        foreach (var attribute in method.AttributeLists.SelectMany(a => a.Attributes))
        {
            var name = attribute.Name.ToString().Split('.').LastOrDefault() ?? attribute.Name.ToString();
            var verb = VerbFromAttributeName(name);
            if (verb is not null)
            {
                httpMethod ??= verb;
                route ??= GetTemplateFromAttributeSyntax(attribute);
            }
            else if (name is "Route" or "RouteAttribute")
            {
                route ??= GetTemplateFromAttributeSyntax(attribute);
            }
        }

        if (semanticModel is not null)
        {
            try
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(method) as IMethodSymbol;
                if (methodSymbol is not null)
                {
                    foreach (var attributeData in methodSymbol.GetAttributes())
                    {
                        var attrName = attributeData.AttributeClass?.Name ?? string.Empty;
                        var verb = VerbFromAttributeName(attrName);
                        if (verb is not null)
                        {
                            httpMethod ??= verb;
                            route ??= GetTemplateFromAttributeData(attributeData);
                        }
                        else if (attrName.Contains("RouteAttribute", StringComparison.Ordinal) ||
                            attrName.Equals("Route", StringComparison.Ordinal))
                        {
                            route ??= GetTemplateFromAttributeData(attributeData);
                        }
                    }
                }
            }
            catch
            {
                // Syntax fallback already used.
            }
        }

        return (httpMethod, route);
    }

    private static string? VerbFromAttributeName(string attributeName)
    {
        var simple = attributeName.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase)
            ? attributeName[..^9]
            : attributeName;

        return simple switch
        {
            "HttpGet" => "GET",
            "HttpPost" => "POST",
            "HttpPut" => "PUT",
            "HttpDelete" => "DELETE",
            "HttpPatch" => "PATCH",
            "HttpHead" => "HEAD",
            "HttpOptions" => "OPTIONS",
            _ => null,
        };
    }

    private static string? GetTemplateFromAttributeSyntax(AttributeSyntax attribute)
    {
        var arguments = attribute.ArgumentList?.Arguments;
        if (arguments is null || arguments.Value.Count == 0)
        {
            return null;
        }

        foreach (var argument in arguments.Value)
        {
            if (argument.NameColon is null ||
                argument.NameColon.Name.Identifier.ValueText.Equals("template", StringComparison.OrdinalIgnoreCase))
            {
                if (argument.Expression is LiteralExpressionSyntax literal &&
                    literal.Token.Value is string value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? GetTemplateFromAttributeData(AttributeData attributeData)
    {
        if (attributeData.ConstructorArguments.Length > 0 &&
            attributeData.ConstructorArguments[0].Value is string positional)
        {
            return positional;
        }

        foreach (var named in attributeData.NamedArguments)
        {
            if ((named.Key.Equals("template", StringComparison.OrdinalIgnoreCase) ||
                 named.Key.Equals("Template", StringComparison.OrdinalIgnoreCase)) &&
                named.Value.Value is string namedValue)
            {
                return namedValue;
            }
        }

        return null;
    }

    private static string CombineRoutes(string prefix, string? actionRoute)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(prefix))
        {
            parts.Add(prefix.Trim('/'));
        }

        if (!string.IsNullOrWhiteSpace(actionRoute))
        {
            var trimmed = actionRoute.Trim();
            if (trimmed.StartsWith("~/", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..];
            }

            if (trimmed.StartsWith('/'))
            {
                return "/" + trimmed.Trim('/');
            }

            parts.Add(trimmed.Trim('/'));
        }

        var combined = string.Join('/', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return combined;
    }

    private static string ExpandRouteTokens(string route, string controllerName, string actionName)
    {
        var controllerToken = controllerName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
            ? controllerName[..^10]
            : controllerName;

        return route
            .Replace("[controller]", controllerToken, StringComparison.OrdinalIgnoreCase)
            .Replace("[action]", actionName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedDirectoryName(string name)
    {
        return name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Migrations", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("generated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedFilePath(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(directory))
        {
            if (IsExcludedDirectoryName(Path.GetFileName(directory)))
            {
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    private static bool IsAutoGeneratedDocument(SyntaxNode root)
    {
        var text = root.SyntaxTree.GetText();
        var prefixLength = Math.Min(1024, text.Length);
        var prefix = text.ToString(TextSpan.FromBounds(0, prefixLength));
        return prefix.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDiagnostic(Diagnostic diagnostic)
    {
        var location = diagnostic.Location;
        var linePosition = location.GetLineSpan().StartLinePosition;
        return $"{location.SourceTree?.FilePath ?? "unknown"}:{linePosition.Line + 1}: {diagnostic.Id}: {diagnostic.GetMessage()}";
    }

    private static void FillProjectMetadata(ProjectAnalysisResult result)
    {
        if (string.IsNullOrEmpty(result.Path) || !File.Exists(result.Path))
        {
            return;
        }

        try
        {
            var document = XDocument.Load(result.Path);
            var root = document.Root;
            var sdk = root?.Attribute("Sdk")?.Value ?? string.Empty;

            string? targetFramework = document
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value;

            if (targetFramework is null)
            {
                var targetFrameworks = document
                    .Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value;
                targetFramework = targetFrameworks
                    ?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
            }

            result.TargetFramework = targetFramework;

            var outputType = document
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "OutputType")?.Value;
            result.ProjectType = DetermineProjectType(sdk, outputType);

            if (result.ProjectReferences.Count == 0)
            {
                var projectDirectory = Path.GetDirectoryName(result.Path)!;
                foreach (var projectReference in document
                    .Descendants()
                    .Where(e => e.Name.LocalName == "ProjectReference"))
                {
                    var include = projectReference.Attribute("Include")?.Value;
                    if (string.IsNullOrWhiteSpace(include))
                    {
                        continue;
                    }

                    var relativePath = include.Replace('\\', Path.DirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, relativePath));
                    result.ProjectReferences.Add(new ProjectReferenceInfo
                    {
                        Name = Path.GetFileNameWithoutExtension(fullPath),
                        Path = fullPath,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Failed to read project metadata: {ex.Message}");
        }
    }

    private static string DetermineProjectType(string sdk, string? outputType)
    {
        if (sdk.Contains(".Web", StringComparison.OrdinalIgnoreCase))
        {
            return "Web";
        }

        if (sdk.Contains(".Worker", StringComparison.OrdinalIgnoreCase))
        {
            return "Worker";
        }

        if (string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase))
        {
            return "Console";
        }

        if (string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
        {
            return "Desktop";
        }

        if (sdk.Contains("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase))
        {
            return "Library";
        }

        return "Unknown";
    }

    private static void SortResults(ProjectAnalysisResult result)
    {
        result.ProjectReferences = result.ProjectReferences
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        result.Controllers = result.Controllers.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        result.Classes = result.Classes.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        result.Interfaces = result.Interfaces.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        result.Records = result.Records.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        result.Enums = result.Enums.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

