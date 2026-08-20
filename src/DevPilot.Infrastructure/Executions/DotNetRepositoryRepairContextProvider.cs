using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Infrastructure.DeveloperAgent;

namespace DevPilot.Infrastructure.Executions;

public sealed class DotNetRepositoryRepairContextProvider : IRepositoryRepairContextProvider
{
    public string? GetCompileRepairContext(
        RepositoryCheck check,
        string workspacePath,
        IReadOnlyList<string> repairFiles)
    {
        if (!string.Equals(check.Ecosystem, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return RoslynContractExtractor.GetAvailablePortDescriptions(workspacePath, repairFiles);
    }
}
