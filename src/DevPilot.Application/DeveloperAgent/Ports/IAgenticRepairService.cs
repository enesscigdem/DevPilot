using DevPilot.Application.DeveloperAgent.Models;

namespace DevPilot.Application.DeveloperAgent.Ports;

/// <summary>
/// Repairs a failing verification the way an interactive coding assistant does: the model reads and searches
/// the workspace, edits files and re-runs the repository's own checks, turn by turn, until they pass or
/// progress stops. Provider-agnostic: the model answers each turn with one JSON action.
/// </summary>
public interface IAgenticRepairService
{
    Task<AgenticRepairOutcome> RunAsync(
        AgenticRepairRequest request,
        CancellationToken cancellationToken = default);
}
