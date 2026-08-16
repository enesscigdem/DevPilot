using System.Text.Json;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;

namespace DevPilot.Application.Executions.Queries.GetExecutionActivity;

public sealed record GetExecutionActivityQuery(Guid ExecutionId);

public sealed class GetExecutionActivityResult
{
    public bool Found { get; set; }
    public string? ErrorMessage { get; set; }
    public IReadOnlyList<ExecutionActivityDto> Activities { get; set; } = Array.Empty<ExecutionActivityDto>();
}

public interface IGetExecutionActivityQueryHandler
{
    Task<GetExecutionActivityResult> HandleAsync(
        GetExecutionActivityQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class GetExecutionActivityQueryHandler : IGetExecutionActivityQueryHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionActivityRepository _activityRepository;

    public GetExecutionActivityQueryHandler(
        IExecutionRepository executionRepository,
        IExecutionActivityRepository activityRepository)
    {
        _executionRepository = executionRepository;
        _activityRepository = activityRepository;
    }

    public async Task<GetExecutionActivityResult> HandleAsync(
        GetExecutionActivityQuery query,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(query.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return new GetExecutionActivityResult
            {
                Found = false,
                ErrorMessage = $"Execution with ID '{query.ExecutionId}' was not found."
            };
        }

        var activities = await _activityRepository
            .GetByExecutionIdAsync(query.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        var dtos = activities.Select(a =>
        {
            ExecutionActivityMetadata? metadata = null;
            if (!string.IsNullOrWhiteSpace(a.MetadataJson))
            {
                try
                {
                    metadata = JsonSerializer.Deserialize<ExecutionActivityMetadata>(a.MetadataJson, JsonOptions);
                }
                catch
                {
                    // Ignore deserialization error for safe fallback
                }
            }

            return new ExecutionActivityDto(
                Id: a.Id,
                ExecutionId: a.ExecutionId,
                Stage: a.Stage.ToString(),
                Status: a.Status.ToString(),
                CreatedAt: a.CreatedAt,
                Message: a.Message,
                Metadata: metadata);
        }).ToList();

        return new GetExecutionActivityResult
        {
            Found = true,
            Activities = dtos
        };
    }
}
