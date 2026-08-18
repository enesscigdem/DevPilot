using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Commands.CommitExecution;
using DevPilot.Application.Executions.Commands.CreatePullRequest;
using DevPilot.Application.Executions.Commands.PushExecution;
using DevPilot.Application.Executions.Commands.RejectExecutionReview;
using DevPilot.Application.Executions.Commands.SyncPullRequest;
using DevPilot.Application.Executions.Queries.GetExecutionActivity;
using DevPilot.Application.Executions.Queries.GetExecutionById;
using DevPilot.Application.Executions.Queries.GetExecutionReview;
using DevPilot.Application.Executions.Queries.GetExecutions;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

public sealed record ApproveExecutionReviewRequest(string ExpectedChangeFingerprint);
public sealed record RejectExecutionReviewRequest(string? Reason);

[ApiController]
[Route("api/executions")]
[Produces("application/json")]
public class ExecutionsController : ControllerBase
{
    private readonly IGetExecutionsQueryHandler _getExecutionsHandler;
    private readonly IGetExecutionByIdQueryHandler _getExecutionByIdHandler;
    private readonly IGetExecutionReviewQueryHandler _getExecutionReviewHandler;
    private readonly IGetExecutionActivityQueryHandler _getExecutionActivityHandler;
    private readonly IApproveExecutionReviewCommandHandler _approveReviewHandler;
    private readonly IRejectExecutionReviewCommandHandler _rejectReviewHandler;
    private readonly ICommitExecutionCommandHandler _commitExecutionHandler;
    private readonly IPushExecutionCommandHandler _pushExecutionHandler;
    private readonly ICreatePullRequestCommandHandler _createPullRequestHandler;

    private readonly ISyncPullRequestCommandHandler _syncPullRequestHandler;

    public ExecutionsController(
        IGetExecutionsQueryHandler getExecutionsHandler,
        IGetExecutionByIdQueryHandler getExecutionByIdHandler,
        IGetExecutionReviewQueryHandler getExecutionReviewHandler,
        IGetExecutionActivityQueryHandler getExecutionActivityHandler,
        IApproveExecutionReviewCommandHandler approveReviewHandler,
        IRejectExecutionReviewCommandHandler rejectReviewHandler,
        ICommitExecutionCommandHandler commitExecutionHandler,
        IPushExecutionCommandHandler pushExecutionHandler,
        ICreatePullRequestCommandHandler createPullRequestHandler,
        ISyncPullRequestCommandHandler syncPullRequestHandler)
    {
        _getExecutionsHandler = getExecutionsHandler;
        _getExecutionByIdHandler = getExecutionByIdHandler;
        _getExecutionReviewHandler = getExecutionReviewHandler;
        _getExecutionActivityHandler = getExecutionActivityHandler;
        _approveReviewHandler = approveReviewHandler;
        _rejectReviewHandler = rejectReviewHandler;
        _commitExecutionHandler = commitExecutionHandler;
        _pushExecutionHandler = pushExecutionHandler;
        _createPullRequestHandler = createPullRequestHandler;
        _syncPullRequestHandler = syncPullRequestHandler;
    }

    [HttpGet]
    public async Task<IActionResult> GetExecutions(
        [FromQuery] Guid? repositoryWorkspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _getExecutionsHandler
            .HandleAsync(new GetExecutionsQuery(repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        return Ok(result.Executions);
    }

    [HttpGet("{id:guid}", Name = nameof(GetExecutionById))]
    public async Task<IActionResult> GetExecutionById(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _getExecutionByIdHandler
            .HandleAsync(new GetExecutionByIdQuery(id, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Found || result.Execution is null)
        {
            return NotFound(new { error = result.ErrorMessage ?? "Execution not found." });
        }

        return Ok(result.Execution);
    }

    [HttpGet("{id:guid}/activity", Name = nameof(GetExecutionActivity))]
    public async Task<IActionResult> GetExecutionActivity(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _getExecutionActivityHandler
            .HandleAsync(new GetExecutionActivityQuery(id, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Found)
        {
            return NotFound(new { error = result.ErrorMessage ?? "Execution not found." });
        }

        return Ok(result.Activities);
    }

    [HttpGet("{id:guid}/review", Name = nameof(GetExecutionReview))]
    public async Task<IActionResult> GetExecutionReview(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _getExecutionReviewHandler
            .HandleAsync(new GetExecutionReviewQuery(id, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            ExecutionReviewResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            ExecutionReviewResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution cannot be reviewed." }),
            ExecutionReviewResultStatus.Success => Ok(result.Review),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }

    [HttpPost("{id:guid}/review/approve", Name = nameof(ApproveExecutionReview))]
    public async Task<IActionResult> ApproveExecutionReview(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        [FromBody] ApproveExecutionReviewRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _approveReviewHandler
            .HandleAsync(new ApproveExecutionReviewCommand(id, request?.ExpectedChangeFingerprint ?? string.Empty, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            ApproveExecutionReviewResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            ApproveExecutionReviewResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution cannot be approved." }),
            ApproveExecutionReviewResultStatus.Success => Ok(result.Decision),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }

    [HttpPost("{id:guid}/review/reject", Name = nameof(RejectExecutionReview))]
    public async Task<IActionResult> RejectExecutionReview(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        [FromBody] RejectExecutionReviewRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _rejectReviewHandler
            .HandleAsync(new RejectExecutionReviewCommand(id, request?.Reason, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            RejectExecutionReviewResultStatus.BadRequest => BadRequest(new { error = result.ErrorMessage ?? "Invalid rejection request." }),
            RejectExecutionReviewResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            RejectExecutionReviewResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution cannot be rejected." }),
            RejectExecutionReviewResultStatus.Success => Ok(result.Decision),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }

    [HttpPost("{id:guid}/commit", Name = nameof(CommitExecution))]
    public async Task<IActionResult> CommitExecution(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _commitExecutionHandler
            .HandleAsync(new CommitExecutionCommand(id, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            CommitExecutionResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            CommitExecutionResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution cannot be committed." }),
            CommitExecutionResultStatus.Success => Ok(result.Response),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }

    [HttpPost("{id:guid}/push", Name = nameof(PushExecution))]
    public async Task<IActionResult> PushExecution(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _pushExecutionHandler
            .HandleAsync(new PushExecutionCommand(id, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            PushExecutionResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            PushExecutionResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution cannot be pushed." }),
            PushExecutionResultStatus.Success => Ok(result.Response),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }

    [HttpPost("{id:guid}/pull-request", Name = nameof(CreatePullRequest))]
    public async Task<IActionResult> CreatePullRequest(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _createPullRequestHandler
            .HandleAsync(new CreatePullRequestCommand(id, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            CreatePullRequestResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            CreatePullRequestResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution cannot create pull request." }),
            CreatePullRequestResultStatus.ExternalFailure => StatusCode(502, new { error = result.ErrorMessage ?? "GitHub API error." }),
            CreatePullRequestResultStatus.Created => StatusCode(201, result.Response),
            CreatePullRequestResultStatus.Success => Ok(result.Response),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }

    [HttpPost("{id:guid}/pull-request/sync", Name = nameof(SyncPullRequest))]
    public async Task<IActionResult> SyncPullRequest(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _syncPullRequestHandler
            .HandleAsync(new DevPilot.Application.Executions.Commands.SyncPullRequest.SyncPullRequestCommand(id, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            DevPilot.Application.Executions.Commands.SyncPullRequest.SyncPullRequestResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            DevPilot.Application.Executions.Commands.SyncPullRequest.SyncPullRequestResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution pull request sync conflict." }),
            DevPilot.Application.Executions.Commands.SyncPullRequest.SyncPullRequestResultStatus.ExternalFailure => StatusCode(502, new { error = result.ErrorMessage ?? "GitHub API synchronization failed.", snapshot = result.Response }),
            DevPilot.Application.Executions.Commands.SyncPullRequest.SyncPullRequestResultStatus.Success => Ok(result.Response),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }

    [HttpPost("{id:guid}/merge", Name = nameof(MergeExecution))]
    public async Task<IActionResult> MergeExecution(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        [FromServices] DevPilot.Application.Executions.Commands.MergeExecution.IMergeExecutionCommandHandler mergeHandler,
        CancellationToken cancellationToken)
    {
        var result = await mergeHandler
            .HandleAsync(new DevPilot.Application.Executions.Commands.MergeExecution.MergeExecutionCommand(id, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            DevPilot.Application.Executions.Commands.MergeExecution.MergeExecutionResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            DevPilot.Application.Executions.Commands.MergeExecution.MergeExecutionResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution cannot be merged." }),
            DevPilot.Application.Executions.Commands.MergeExecution.MergeExecutionResultStatus.ExternalFailure => StatusCode(502, new { error = result.ErrorMessage ?? "External GitHub merge error." }),
            DevPilot.Application.Executions.Commands.MergeExecution.MergeExecutionResultStatus.Created => StatusCode(201, result.Response),
            DevPilot.Application.Executions.Commands.MergeExecution.MergeExecutionResultStatus.Success => Ok(result.Response),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }
}
