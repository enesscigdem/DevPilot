using System.Text.Json;
using DevPilot.Application.ProjectBrain.Models;
using DevPilot.Application.ProjectBrain.Ports;

namespace DevPilot.Application.ProjectBrain.Queries.GetBrainConversations;

public sealed record GetBrainConversationsQuery(Guid WorkspaceId);

public interface IGetBrainConversationsQueryHandler
{
    Task<IReadOnlyList<BrainConversationDto>> HandleAsync(
        GetBrainConversationsQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class GetBrainConversationsQueryHandler : IGetBrainConversationsQueryHandler
{
    private readonly IProjectBrainConversationRepository _repository;

    public GetBrainConversationsQueryHandler(IProjectBrainConversationRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<BrainConversationDto>> HandleAsync(
        GetBrainConversationsQuery query,
        CancellationToken cancellationToken = default)
    {
        var conversations = await _repository
            .GetByWorkspaceIdAsync(query.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        return conversations.Select(c => new BrainConversationDto
        {
            Id = c.Id,
            RepositoryWorkspaceId = c.RepositoryWorkspaceId,
            Title = c.Title,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
            MessageCount = c.Messages.Count,
        }).ToList();
    }
}
