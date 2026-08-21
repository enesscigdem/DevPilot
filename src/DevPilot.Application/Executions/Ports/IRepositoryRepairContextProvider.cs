using DevPilot.Application.Executions.Models;

namespace DevPilot.Application.Executions.Ports;

/// <summary>
/// Optional language-aware context for a focused repair. The execution core remains independent
/// of Roslyn or any other ecosystem-specific analysis implementation.
/// </summary>
public interface IRepositoryRepairContextProvider
{
    string? GetCompileRepairContext(
        RepositoryCheck check,
        string workspacePath,
        IReadOnlyList<string> repairFiles);
}
