using DevPilot.Application.ProjectBrain.Ports;

namespace DevPilot.Application.ProjectBrain.Commands.DeleteBrainConversation;

public sealed record DeleteBrainConversationCommand(Guid WorkspaceId, Guid ConversationId);

public interface IDeleteBrainConversationCommandHandler
{
    Task<bool> HandleAsync(
        DeleteBrainConversationCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class DeleteBrainConversationCommandHandler : IDeleteBrainConversationCommandHandler
{
    private readonly IProjectBrainConversationRepository _repository;

    public DeleteBrainConversationCommandHandler(IProjectBrainConversationRepository repository)
    {
        _repository = repository;
    }

    public async Task<bool> HandleAsync(
        DeleteBrainConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _repository
            .GetByIdAsync(command.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null || conversation.RepositoryWorkspaceId != command.WorkspaceId)
        {
            return false;
        }

        await _repository.DeleteAsync(conversation, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
