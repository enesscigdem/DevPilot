using DevPilot.Application.ProjectBrain.Models;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Domain.ProjectBrain.Entities;

namespace DevPilot.Application.ProjectBrain.Commands.CreateBrainConversation;

public sealed record CreateBrainConversationCommand(Guid WorkspaceId, string? Title = null);

public interface ICreateBrainConversationCommandHandler
{
    Task<BrainConversationDto> HandleAsync(
        CreateBrainConversationCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class CreateBrainConversationCommandHandler : ICreateBrainConversationCommandHandler
{
    private readonly IProjectBrainConversationRepository _repository;

    public CreateBrainConversationCommandHandler(IProjectBrainConversationRepository repository)
    {
        _repository = repository;
    }

    public async Task<BrainConversationDto> HandleAsync(
        CreateBrainConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        var conversation = new ProjectBrainConversation
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = command.WorkspaceId,
            Title = string.IsNullOrWhiteSpace(command.Title) ? "New Conversation" : command.Title.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await _repository.AddAsync(conversation, cancellationToken).ConfigureAwait(false);

        return new BrainConversationDto
        {
            Id = conversation.Id,
            RepositoryWorkspaceId = conversation.RepositoryWorkspaceId,
            Title = conversation.Title,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt,
            MessageCount = 0,
        };
    }
}
