using DevPilot.Application.ProjectBrain.Models;

namespace DevPilot.Application.ProjectBrain.Commands.AskBrain;

public sealed record AskBrainCommand(
    Guid WorkspaceId,
    string Question);

public interface IAskBrainCommandHandler
{
    Task<BrainChatResult> HandleAsync(
        AskBrainCommand command,
        CancellationToken cancellationToken = default);
}
