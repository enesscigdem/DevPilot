namespace DevPilot.Application.CodeAnalysis;

public sealed class RepositoryAnalysisRequest
{
    public string WorkspacePath { get; set; } = string.Empty;
}

public sealed class RepositoryAnalysisResult
{
    public bool Success { get; set; }

    public string? Error { get; set; }

    public List<string> Warnings { get; set; } = new();

    public List<SolutionAnalysisResult> Solutions { get; set; } = new();

    public List<ProjectAnalysisResult> StandaloneProjects { get; set; } = new();
}

public sealed class SolutionAnalysisResult
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public List<ProjectAnalysisResult> Projects { get; set; } = new();

    public List<string> Warnings { get; set; } = new();
}

public sealed class ProjectAnalysisResult
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string ProjectType { get; set; } = "Unknown";

    public string? TargetFramework { get; set; }

    public List<ProjectReferenceInfo> ProjectReferences { get; set; } = new();

    public List<ControllerAnalysisResult> Controllers { get; set; } = new();

    public List<TypeAnalysisResult> Classes { get; set; } = new();

    public List<TypeAnalysisResult> Interfaces { get; set; } = new();

    public List<TypeAnalysisResult> Records { get; set; } = new();

    public List<EnumAnalysisResult> Enums { get; set; } = new();

    public bool CompilationSucceeded { get; set; }

    public List<string> CompilationErrors { get; set; } = new();

    public List<string> Warnings { get; set; } = new();
}

public sealed class ProjectReferenceInfo
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}

public class TypeAnalysisResult
{
    public string Name { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string? BaseType { get; set; }

    public List<MethodAnalysisResult> Methods { get; set; } = new();

    public List<MethodAnalysisResult> Constructors { get; set; } = new();

    public List<PropertyAnalysisResult> Properties { get; set; } = new();
}

public sealed class ControllerAnalysisResult : TypeAnalysisResult
{
    public List<ControllerActionAnalysisResult> Actions { get; set; } = new();
}

public sealed class EnumAnalysisResult
{
    public string Name { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public List<string> Values { get; set; } = new();
}

public class MethodAnalysisResult
{
    public string Name { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string? ReturnType { get; set; }

    public string Accessibility { get; set; } = "public";

    public bool IsStatic { get; set; }

    public bool IsConstructor { get; set; }
}

public sealed class PropertyAnalysisResult
{
    public string Name { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string? TypeName { get; set; }

    public string Accessibility { get; set; } = "public";

    public bool IsStatic { get; set; }
}

public sealed class ControllerActionAnalysisResult
{
    public string Name { get; set; } = string.Empty;

    public string? HttpMethod { get; set; }

    public string? RouteTemplate { get; set; }

    public string SourcePath { get; set; } = string.Empty;
}
