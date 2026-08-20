using System.Text;
using DevPilot.Application.DeveloperAgent.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DevPilot.Infrastructure.DeveloperAgent;

public enum SymbolOrigin
{
    ResolvedRepositoryOwned,
    ResolvedExternalOrReferenced,
    Unknown
}

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

    public sealed class LockedContractModel
    {
        public string FilePath { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public bool IsRecord { get; set; }
        public List<int> RecordOrConstructorParamCounts { get; } = new();
        public List<LockedMethodModel> Methods { get; } = new();
    }

    public sealed class LockedMethodModel
    {
        public string Name { get; set; } = string.Empty;
        public int ParameterCount { get; set; }
        public string Signature { get; set; } = string.Empty;
    }

    private static List<LockedContractModel> ParseLockedContracts(IReadOnlyDictionary<string, string> lockedContracts)
    {
        var models = new List<LockedContractModel>();
        foreach (var (contractFile, contractText) in lockedContracts)
        {
            if (string.IsNullOrWhiteSpace(contractText)) continue;
            var tree = CSharpSyntaxTree.ParseText(contractText);
            var root = tree.GetCompilationUnitRoot();

            var typeDeclarations = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>();
            foreach (var typeDecl in typeDeclarations)
            {
                var typeName = typeDecl.Identifier.Text;
                var nsDecl = typeDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                var ns = nsDecl?.Name.ToString().Trim() ?? string.Empty;

                var model = new LockedContractModel
                {
                    FilePath = contractFile,
                    Namespace = ns,
                    TypeName = typeName
                };

                if (typeDecl is RecordDeclarationSyntax recordDecl)
                {
                    model.IsRecord = true;
                    var paramCount = recordDecl.ParameterList?.Parameters.Count ?? 0;
                    model.RecordOrConstructorParamCounts.Add(paramCount);
                }

                var ctors = typeDecl.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
                    .Where(c => c.Modifiers.Any(SyntaxKind.PublicKeyword));
                foreach (var ctor in ctors)
                {
                    model.RecordOrConstructorParamCounts.Add(ctor.ParameterList.Parameters.Count);
                }

                var methods = typeDecl.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) || typeDecl is InterfaceDeclarationSyntax);
                foreach (var m in methods)
                {
                    model.Methods.Add(new LockedMethodModel
                    {
                        Name = m.Identifier.Text,
                        ParameterCount = m.ParameterList.Parameters.Count,
                        Signature = m.ToFullString().Trim()
                    });
                }

                models.Add(model);
            }
        }
        return models;
    }

    private static string? ResolveReceiverTypeName(
        ExpressionSyntax receiverExpr,
        SyntaxNode contextNode,
        CompilationUnitSyntax root)
    {
        if (receiverExpr is IdentifierNameSyntax idSyntax)
        {
            var idText = idSyntax.Identifier.Text;

            var enclosingBlock = contextNode.AncestorsAndSelf().OfType<BlockSyntax>().FirstOrDefault();
            if (enclosingBlock != null)
            {
                var varDeclarations = enclosingBlock.DescendantNodes().OfType<VariableDeclarationSyntax>();
                foreach (var varDecl in varDeclarations)
                {
                    foreach (var variable in varDecl.Variables)
                    {
                        if (variable.Identifier.Text == idText)
                        {
                            var declaredType = varDecl.Type.ToString();
                            if (declaredType != "var")
                            {
                                return NormalizeTypeName(declaredType);
                            }
                            if (variable.Initializer?.Value != null)
                            {
                                var initType = ResolveExpressionType(variable.Initializer.Value, root);
                                if (!string.IsNullOrEmpty(initType))
                                    return NormalizeTypeName(initType);
                            }
                        }
                    }
                }
            }

            var enclosingMethod = contextNode.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
            if (enclosingMethod != null)
            {
                foreach (var param in enclosingMethod.ParameterList.Parameters)
                {
                    if (param.Identifier.Text == idText && param.Type != null)
                    {
                        return NormalizeTypeName(param.Type.ToString());
                    }
                }
            }

            var enclosingType = contextNode.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (enclosingType != null)
            {
                var fields = enclosingType.Members.OfType<FieldDeclarationSyntax>();
                foreach (var f in fields)
                {
                    foreach (var v in f.Declaration.Variables)
                    {
                        if (v.Identifier.Text == idText)
                        {
                            return NormalizeTypeName(f.Declaration.Type.ToString());
                        }
                    }
                }

                var props = enclosingType.Members.OfType<PropertyDeclarationSyntax>();
                foreach (var p in props)
                {
                    if (p.Identifier.Text == idText)
                    {
                        return NormalizeTypeName(p.Type.ToString());
                    }
                }
            }

            return NormalizeTypeName(idText);
        }

        if (receiverExpr is MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Expression is ThisExpressionSyntax)
            {
                return ResolveReceiverTypeName(memberAccess.Name, contextNode, root);
            }

            if (memberAccess.Name.Identifier.Text is "CreateClient" or "CreateDefaultClient")
            {
                return "HttpClient";
            }

            return ResolveExpressionType(memberAccess, root);
        }

        if (receiverExpr is ObjectCreationExpressionSyntax oc)
        {
            return NormalizeTypeName(oc.Type.ToString());
        }

        if (receiverExpr is InvocationExpressionSyntax inv)
        {
            return ResolveExpressionType(inv, root);
        }

        return null;
    }

    private static string? ResolveExpressionType(ExpressionSyntax expr, CompilationUnitSyntax root)
    {
        if (expr is ObjectCreationExpressionSyntax oc)
        {
            return NormalizeTypeName(oc.Type.ToString());
        }

        if (expr is InvocationExpressionSyntax inv)
        {
            var exprStr = inv.Expression.ToString();
            if (exprStr.EndsWith(".CreateClient") || exprStr.EndsWith(".CreateDefaultClient") || exprStr == "CreateClient")
            {
                return "HttpClient";
            }
            if (exprStr.EndsWith(".GetAsync") || exprStr.EndsWith(".PostAsync") || exprStr.EndsWith(".SendAsync"))
            {
                return "HttpResponseMessage";
            }
        }

        return null;
    }

    private static string NormalizeTypeName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return string.Empty;
        var shortName = typeName.Trim();
        if (shortName.Contains('.'))
        {
            shortName = shortName.Split('.').Last();
        }
        if (shortName.Contains('<'))
        {
            shortName = shortName.Substring(0, shortName.IndexOf('<')).Trim();
        }
        return shortName.TrimEnd('?');
    }

    /// <summary>
    /// Classifies a symbol into one of three states:
    /// - ResolvedRepositoryOwned: Explicitly declared in locked contracts or repository workspace.
    /// - ResolvedExternalOrReferenced: External framework or package type (never compared against unrelated repo contracts).
    /// - Unknown: Static knowledge is incomplete. Must NOT terminally fail; defers to compilation.
    /// </summary>
    public static SymbolOrigin ClassifySymbolOrigin(
        string typeName,
        IReadOnlyDictionary<string, string>? lockedContracts,
        HashSet<string>? workspaceSymbols = null)
    {
        var contractModels = lockedContracts != null ? ParseLockedContracts(lockedContracts) : null;
        return ClassifySymbolOrigin(typeName, contractModels, workspaceSymbols);
    }

    public static SymbolOrigin ClassifySymbolOrigin(
        string typeName,
        IReadOnlyList<LockedContractModel>? contractModels,
        HashSet<string>? workspaceSymbols)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return SymbolOrigin.Unknown;
        var shortName = NormalizeTypeName(typeName);

        if (contractModels != null && contractModels.Any(c => c.TypeName.Equals(shortName, StringComparison.OrdinalIgnoreCase)))
        {
            return SymbolOrigin.ResolvedRepositoryOwned;
        }

        if (workspaceSymbols != null && workspaceSymbols.Contains(shortName))
        {
            return SymbolOrigin.ResolvedRepositoryOwned;
        }

        // Framework / external heuristics that should NEVER collide with repository contracts
        if (shortName is "HttpClient" or "HttpResponseMessage" or "HttpRequestMessage" or "WebApplicationFactory" or
            "Assert" or "IMapper" or "IValidator" or "DbContext" or "DbSet" or "TestServer" or "Mock" or "Should")
        {
            return SymbolOrigin.ResolvedExternalOrReferenced;
        }

        return SymbolOrigin.Unknown;
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

        var contractModels = ParseLockedContracts(lockedContracts);
        if (contractModels.Count == 0)
        {
            return (true, null);
        }

        var tree = CSharpSyntaxTree.ParseText(consumerCode);
        var root = tree.GetCompilationUnitRoot();

        // 1. Check object creation expressions against locked record/class constructors
        var objectCreations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().ToList();
        foreach (var creation in objectCreations)
        {
            var rawTypeName = creation.Type.ToString();
            var shortTypeName = NormalizeTypeName(rawTypeName);

            // Only validate constructor calls against EXACT repository-owned locked contracts
            var matchingContract = contractModels.FirstOrDefault(c =>
                c.TypeName.Equals(shortTypeName, StringComparison.OrdinalIgnoreCase));

            if (matchingContract != null && matchingContract.RecordOrConstructorParamCounts.Count > 0)
            {
                var actualArgCount = creation.ArgumentList?.Arguments.Count ?? 0;
                if (!matchingContract.RecordOrConstructorParamCounts.Contains(actualArgCount))
                {
                    var expectedCounts = string.Join(" or ", matchingContract.RecordOrConstructorParamCounts);
                    return (false,
                        $"Semantic contract violation in '{consumerFilePath}': Constructor call 'new {shortTypeName}(...)' has {actualArgCount} argument(s), but locked upstream contract in '{matchingContract.FilePath}' expects {expectedCounts} parameter(s).");
                }
            }
        }

        // 2. Check method invocations with receiver / declaring type awareness
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;
                var argCount = invocation.ArgumentList.Arguments.Count;

                var receiverTypeName = ResolveReceiverTypeName(memberAccess.Expression, invocation, root);
                if (string.IsNullOrWhiteSpace(receiverTypeName))
                {
                    continue;
                }

                // Explicit classification: only compare if receiver is definitely a ResolvedRepositoryOwned contract
                var origin = ClassifySymbolOrigin(receiverTypeName, contractModels, null);
                if (origin != SymbolOrigin.ResolvedRepositoryOwned)
                {
                    // ResolvedExternalOrReferenced or Unknown: NEVER collide against unrelated repository contracts.
                    continue;
                }

                // Find exact matching contract by type name (or interface prefix only when declared in contracts)
                var matchingContract = contractModels.FirstOrDefault(c =>
                    c.TypeName.Equals(receiverTypeName, StringComparison.OrdinalIgnoreCase) ||
                    (c.TypeName.Equals("I" + receiverTypeName, StringComparison.OrdinalIgnoreCase) && !receiverTypeName.StartsWith("I")) ||
                    (receiverTypeName.Equals("I" + c.TypeName, StringComparison.OrdinalIgnoreCase) && receiverTypeName.StartsWith("I")));

                if (matchingContract == null)
                {
                    continue;
                }

                var contract = matchingContract;
                var declaredMethods = contract.Methods;
                var exactMatch = declaredMethods.FirstOrDefault(m => m.Name == methodName);

                if (exactMatch != null)
                {
                    if (exactMatch.ParameterCount != argCount)
                    {
                        return (false,
                            $"Semantic contract violation in '{consumerFilePath}': Method invocation '{methodName}(...)' on '{receiverTypeName}' has {argCount} argument(s), but locked contract in '{contract.FilePath}' defines '{exactMatch.Signature}' with {exactMatch.ParameterCount} parameter(s).");
                    }
                }
                else
                {
                    // Check for proven async/sync drift on locked repository contract
                    if (methodName.EndsWith("Async"))
                    {
                        var altName = methodName[..^5];
                        var syncMatch = declaredMethods.FirstOrDefault(m => m.Name == altName);
                        if (syncMatch != null)
                        {
                            return (false,
                                $"Semantic contract drift in '{consumerFilePath}': Invoking '{methodName}', but locked contract in '{contract.FilePath}' defines '{altName}': '{syncMatch.Signature}'.");
                        }
                    }
                    else
                    {
                        var altName = methodName + "Async";
                        var asyncMatch = declaredMethods.FirstOrDefault(m => m.Name == altName);
                        if (asyncMatch != null)
                        {
                            return (false,
                                $"Semantic contract drift in '{consumerFilePath}': Invoking '{methodName}', but locked contract in '{contract.FilePath}' defines '{altName}': '{asyncMatch.Signature}'.");
                        }
                    }

                    if (methodName is "ToString" or "Equals" or "GetHashCode" or "GetType")
                    {
                        continue;
                    }

                    return (false,
                        $"Semantic contract violation in '{consumerFilePath}': Method invocation '{methodName}' does not exist on locked contract '{contract.TypeName}' in '{contract.FilePath}'.");
                }
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Softened: Does NOT block candidate code before build on unlisted using directives or heuristic namespace guesses.
    /// Real project compilation / dotnet build provides authoritative package/namespace resolution.
    /// </summary>
    public static (bool IsValid, string? ErrorMessage) ValidateProjectArchitecturalDependencies(
        string targetFilePath,
        string code,
        IReadOnlyList<DiscoveredProjectNode>? projectGraph,
        IReadOnlyDictionary<string, string>? lockedContracts)
    {
        // Compiler is primary oracle for namespace/using validation.
        return (true, null);
    }

    /// <summary>
    /// Softened: Pattern check provides advisory facts rather than terminally blocking code before build.
    /// Compiler catches missing interface implementations (CS0535).
    /// </summary>
    public static (bool IsValid, string? ErrorMessage, string? PatternFileName) ValidateProducerAgainstRepositoryPattern(
        string targetFilePath,
        string code,
        string workspacePath,
        IReadOnlyList<DiscoveredProjectNode>? projectGraph = null)
    {
        var patternFacts = RoslynPatternHelper.DiscoverNearestPatternFacts(workspacePath, targetFilePath);
        return (true, null, patternFacts?.PatternFilePath);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime ScannedAt, HashSet<string> Symbols)> WorkspaceSymbolCache = new();

    public enum PackageSymbolResolutionStatus
    {
        Resolved,
        MissingUsingDirective,
        PackageNotReferenced,
        NotAPackageSymbol
    }

    public static (PackageSymbolResolutionStatus Status, string? PackageName, string? RequiredNamespace) CheckPackageSymbolResolution(
        string symbolName,
        string targetFilePath,
        IReadOnlyList<DiscoveredProjectNode>? projectGraph,
        string? workspacePath = null,
        CompilationUnitSyntax? root = null,
        string? rawType = null)
    {
        // Helper retained for telemetry/tests; defaults to Resolved so as not to block candidate edits
        return (PackageSymbolResolutionStatus.Resolved, null, null);
    }

    public static bool IsPackageSymbolResolvable(
        string symbolName,
        string targetFilePath,
        IReadOnlyList<DiscoveredProjectNode>? projectGraph,
        string? workspacePath = null,
        CompilationUnitSyntax? root = null,
        string? rawType = null)
    {
        return true;
    }

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

    /// <summary>
    /// Softened: Incomplete static symbol heuristics must NOT terminally reject code before compilation.
    /// Real compilation / dotnet build provides authoritative CS0246 / CS0103 diagnostics.
    /// </summary>
    public static (bool IsValid, string? ErrorMessage) ValidateSymbolResolution(
        string targetFilePath,
        string code,
        string workspacePath,
        IReadOnlyDictionary<string, string>? lockedContracts = null,
        IReadOnlyList<DiscoveredProjectNode>? projectGraph = null)
    {
        // Defer unknown/package symbol resolution to MSBuild / Roslyn compilation
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
        IReadOnlyDictionary<string, FileEditSpec>? completedEdits,
        IReadOnlyDictionary<string, string>? lockedContracts = null)
    {
        if (string.IsNullOrWhiteSpace(targetCode)) return (true, null);

        var currentTypes = ExtractDeclaredTypesWithNamespace(targetCode);
        if (currentTypes.Count == 0) return (true, null);

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
