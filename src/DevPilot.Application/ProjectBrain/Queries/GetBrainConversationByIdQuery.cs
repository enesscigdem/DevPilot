using System.Text.Json;
using DevPilot.Application.ProjectBrain.Models;
using DevPilot.Application.ProjectBrain.Ports;

namespace DevPilot.Application.ProjectBrain.Queries.GetBrainConversationById;

public sealed record GetBrainConversationByIdQuery(Guid WorkspaceId, Guid ConversationId);

public interface IGetBrainConversationByIdQueryHandler
{
    Task<BrainConversationDetailDto?> HandleAsync(
        GetBrainConversationByIdQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class GetBrainConversationByIdQueryHandler : IGetBrainConversationByIdQueryHandler
{
    private readonly IProjectBrainConversationRepository _repository;

    public GetBrainConversationByIdQueryHandler(IProjectBrainConversationRepository repository)
    {
        _repository = repository;
    }

    public async Task<BrainConversationDetailDto?> HandleAsync(
        GetBrainConversationByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _repository
            .GetByIdWithMessagesAsync(query.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null || conversation.RepositoryWorkspaceId != query.WorkspaceId)
        {
            return null;
        }

        return new BrainConversationDetailDto
        {
            Id = conversation.Id,
            RepositoryWorkspaceId = conversation.RepositoryWorkspaceId,
            Title = conversation.Title,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt,
            Messages = conversation.Messages.Select(m =>
            {
                List<BrainCitationDto>? citations = null;
                if (!string.IsNullOrWhiteSpace(m.CitationsJson))
                {
                    try { citations = JsonSerializer.Deserialize<List<BrainCitationDto>>(m.CitationsJson); } catch { }
                }

                List<BrainContextFileDto>? contextFiles = null;
                if (!string.IsNullOrWhiteSpace(m.ContextFilesJson))
                {
                    try { contextFiles = JsonSerializer.Deserialize<List<BrainContextFileDto>>(m.ContextFilesJson); } catch { }
                }

                return new BrainMessageDto
                {
                    Id = m.Id,
                    ConversationId = m.ConversationId,
                    Role = m.Role,
                    Content = m.Content,
                    Confidence = m.Confidence,
                    Elapsed = m.Elapsed,
                    Citations = citations,
                    ContextFiles = contextFiles,
                    CreatedAt = m.CreatedAt,
                };
            }).ToList(),
        };
    }
}
