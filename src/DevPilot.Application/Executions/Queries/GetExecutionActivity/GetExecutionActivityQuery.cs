using System.Text.Json;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;

namespace DevPilot.Application.Executions.Queries.GetExecutionActivity;

public sealed record GetExecutionActivityQuery(Guid ExecutionId, Guid? RepositoryWorkspaceId = null);

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

    private static readonly System.Text.RegularExpressions.Regex AbsolutePathRegex =
        new(@"([a-zA-Z]:\\[^\s""]+|/(?:home|Users|tmp|var)/[^\s""]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

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

        if (query.RepositoryWorkspaceId.HasValue &&
            execution.DevelopmentTask.RepositoryWorkspaceId != query.RepositoryWorkspaceId.Value)
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

            var sanitizedMessage = SanitizeMessage(a.Message);

            return new ExecutionActivityDto(
                Id: a.Id,
                ExecutionId: a.ExecutionId,
                Stage: a.Stage.ToString(),
                Status: a.Status.ToString(),
                CreatedAt: a.CreatedAt,
                Message: sanitizedMessage,
                Metadata: metadata);
        }).ToList();

        return new GetExecutionActivityResult
        {
            Found = true,
            Activities = dtos
        };
    }

    public static string SanitizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        return AbsolutePathRegex.Replace(message, "[path]");
    }
}
