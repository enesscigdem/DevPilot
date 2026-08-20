using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DevPilot.Infrastructure.DeveloperAgent;

public sealed record DiscoveredPatternFacts(
    string? PatternFilePath,
    string? ExpectedHandlerMethodName,
    bool UsesMediatR,
    bool ImplementsCustomInterface,
    string? Skeleton);

public static class RoslynPatternHelper
{
    public static DiscoveredPatternFacts? DiscoverNearestPatternFacts(string workspacePath, string targetFilePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || string.IsNullOrWhiteSpace(targetFilePath))
            return null;

        var role = ClassifyRole(targetFilePath);
        if (role == null) return null;

        var targetDir = Path.GetDirectoryName(Path.Combine(workspacePath, targetFilePath.Replace('/', Path.DirectorySeparatorChar)));
        var effectiveDir = targetDir;

        // Walk up to find nearest existing ancestor directory within workspacePath
        while (!string.IsNullOrEmpty(effectiveDir) && !Directory.Exists(effectiveDir) && effectiveDir.StartsWith(workspacePath, StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(effectiveDir);
            effectiveDir = parent?.FullName;
        }

        if (string.IsNullOrEmpty(effectiveDir) || !Directory.Exists(effectiveDir))
        {
            effectiveDir = workspacePath;
        }

        var candidateFiles = new List<string>();
        if (Directory.Exists(effectiveDir))
        {
            candidateFiles.AddRange(Directory.GetFiles(effectiveDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Equals(Path.GetFileName(targetFilePath), StringComparison.OrdinalIgnoreCase))
                .Take(40));
        }

        // Search project root or workspace if role match not found yet
        if (candidateFiles.Count == 0 || !candidateFiles.Any(f => ClassifyRole(f) == role))
        {
            var normalized = targetFilePath.Replace('\\', '/').TrimStart('/');
            var segments = normalized.Split('/');
            var projectSubDir = segments.Length >= 2 ? Path.Combine(workspacePath, segments[0], segments[1]) : Path.Combine(workspacePath, segments[0]);
            var searchRoot = Directory.Exists(projectSubDir) ? projectSubDir : workspacePath;

            if (Directory.Exists(searchRoot))
            {
                candidateFiles.AddRange(Directory.GetFiles(searchRoot, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).Equals(Path.GetFileName(targetFilePath), StringComparison.OrdinalIgnoreCase))
                    .Take(50));
            }
        }

        var matchingPatternFile = candidateFiles.FirstOrDefault(f => ClassifyRole(f) == role);
        if (matchingPatternFile == null && candidateFiles.Count > 0)
        {
            matchingPatternFile = candidateFiles[0];
        }

        if (matchingPatternFile != null && File.Exists(matchingPatternFile))
        {
            try
            {
                var relPath = Path.GetRelativePath(workspacePath, matchingPatternFile).Replace('\\', '/');
                var content = File.ReadAllText(matchingPatternFile);
                var skeleton = ExtractStructuralSkeleton(Path.GetFileName(matchingPatternFile), content);

                var tree = CSharpSyntaxTree.ParseText(content);
                var root = tree.GetCompilationUnitRoot();

                string? expectedMethod = null;
                var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) || m.Parent is InterfaceDeclarationSyntax)
                    .ToList();

                if (methods.Any(m => m.Identifier.Text.Equals("HandleAsync", StringComparison.OrdinalIgnoreCase)))
                {
                    expectedMethod = "HandleAsync";
                }
                else if (methods.Any(m => m.Identifier.Text.Equals("Handle", StringComparison.OrdinalIgnoreCase)))
                {
                    expectedMethod = "Handle";
                }
                else if (methods.Count > 0)
                {
                    expectedMethod = methods[0].Identifier.Text;
                }

                var baseTypes = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                    .Where(b => b.BaseList != null)
                    .SelectMany(b => b.BaseList!.Types)
                    .Select(t => t.Type.ToString())
                    .ToList();

                var usesMediatR = root.Usings.Any(u => u.Name?.ToString().Contains("MediatR", StringComparison.OrdinalIgnoreCase) == true) ||
                                  baseTypes.Any(bt => bt.Contains("IRequest", StringComparison.OrdinalIgnoreCase) ||
                                                      bt.Contains("IRequestHandler", StringComparison.OrdinalIgnoreCase) ||
                                                      bt.Contains("INotification", StringComparison.OrdinalIgnoreCase));

                var implementsCustom = baseTypes.Any(bt => !bt.Contains("IRequest", StringComparison.OrdinalIgnoreCase) &&
                                                           !bt.Contains("IRequestHandler", StringComparison.OrdinalIgnoreCase) &&
                                                           bt.StartsWith("I", StringComparison.OrdinalIgnoreCase));

                return new DiscoveredPatternFacts(
                    PatternFilePath: relPath,
                    ExpectedHandlerMethodName: expectedMethod,
                    UsesMediatR: usesMediatR,
                    ImplementsCustomInterface: implementsCustom,
                    Skeleton: skeleton);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public static string? DiscoverNearestPattern(string workspacePath, string targetFilePath, out string? matchingPatternFilePath)
    {
        matchingPatternFilePath = null;
        var facts = DiscoverNearestPatternFacts(workspacePath, targetFilePath);
        if (facts != null)
        {
            matchingPatternFilePath = facts.PatternFilePath;
            return facts.Skeleton;
        }
        return null;
    }

    public static string? DiscoverNearestPattern(string workspacePath, string targetFilePath)
        => DiscoverNearestPattern(workspacePath, targetFilePath, out _);

    public static string? ClassifyRole(string fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath)) return null;
        var fileName = Path.GetFileNameWithoutExtension(fileNameOrPath);

        if (fileName.EndsWith("QueryHandler", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("CommandHandler", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("Handler", StringComparison.OrdinalIgnoreCase))
            return "Handler";
        if (fileName.EndsWith("Query", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("Command", StringComparison.OrdinalIgnoreCase))
            return "Request";
        if (fileName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
            return "Controller";
        if (fileName.EndsWith("Repository", StringComparison.OrdinalIgnoreCase))
            return "Repository";
        if (fileName.EndsWith("Dto", StringComparison.OrdinalIgnoreCase))
            return "Dto";
        if (fileName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("Test", StringComparison.OrdinalIgnoreCase))
            return "Test";

        return null;
    }

    private static string ExtractStructuralSkeleton(string fileName, string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();

        var sb = new StringBuilder();
        sb.AppendLine($"// Reference pattern from repository: '{fileName}'");

        var usings = root.Usings.Select(u => u.ToFullString().Trim()).Take(6).ToList();
        foreach (var u in usings)
        {
            sb.AppendLine(u);
        }
        sb.AppendLine();

        var typeDeclarations = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Take(2);
        foreach (var typeDecl in typeDeclarations)
        {
            if (typeDecl is RecordDeclarationSyntax recordDecl)
            {
                var paramList = recordDecl.ParameterList?.ToFullString().Trim() ?? "()";
                var baseList = recordDecl.BaseList != null ? " " + recordDecl.BaseList.ToFullString().Trim() : "";
                sb.AppendLine($"public sealed record {recordDecl.Identifier.Text}{paramList}{baseList};");
                continue;
            }

            var typeKind = typeDecl is InterfaceDeclarationSyntax ? "interface" : "class";
            var baseListStr = typeDecl.BaseList != null ? " " + typeDecl.BaseList.ToFullString().Trim() : "";
            sb.AppendLine($"public sealed {typeKind} {typeDecl.Identifier.Text}{baseListStr}");
            sb.AppendLine("{");

            // Extract primary constructor or first constructor
            var ctor = typeDecl.DescendantNodes().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
            if (ctor != null)
            {
                sb.AppendLine($"    public {ctor.Identifier.Text}{ctor.ParameterList.ToFullString().Trim()} {{ ... }}");
            }

            // Extract main methods (signatures only)
            var methods = typeDecl.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword))).Take(2);
            foreach (var m in methods)
            {
                sb.AppendLine($"    public {m.ReturnType.ToFullString().Trim()} {m.Identifier.Text}{m.ParameterList.ToFullString().Trim()} {{ ... }}");
            }

            sb.AppendLine("}");
        }

        return sb.ToString().Trim();
    }
}
