using DevPilot.Application.DeveloperAgent.Models;

namespace DevPilot.Application.DeveloperAgent.Ports;

public interface IDeveloperAgent
{
    Task<DeveloperAgentResult> GenerateAndApplyEditsAsync(
        DeveloperAgentRequest request,
        CancellationToken cancellationToken = default);
}
