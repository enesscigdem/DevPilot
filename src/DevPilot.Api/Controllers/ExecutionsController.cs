using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Commands.CommitExecution;
using DevPilot.Application.Executions.Commands.CreatePullRequest;
using DevPilot.Application.Executions.Commands.PushExecution;
using DevPilot.Application.Executions.Commands.RejectExecutionReview;
using DevPilot.Application.Executions.Commands.RequestExecutionChanges;
using DevPilot.Application.Executions.Commands.SyncPullRequest;
using DevPilot.Application.Executions.Queries.GetExecutionActivity;
using DevPilot.Application.Executions.Queries.GetExecutionById;
using DevPilot.Application.Executions.Queries.GetExecutionReview;
using DevPilot.Application.Executions.Queries.GetExecutionRevisionDiff;
using DevPilot.Application.Executions.Queries.GetExecutions;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

public sealed record ApproveExecutionReviewRequest(string ExpectedChangeFingerprint, bool VisualAcknowledged = false);
public sealed record RejectExecutionReviewRequest(string? Reason);
public sealed record RequestExecutionChangesRequest(string? Feedback);

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
            .HandleAsync(new ApproveExecutionReviewCommand(id, request?.ExpectedChangeFingerprint ?? string.Empty, repositoryWorkspaceId, request?.VisualAcknowledged ?? false), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            ApproveExecutionReviewResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            ApproveExecutionReviewResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution cannot be approved." }),
            ApproveExecutionReviewResultStatus.Success => Ok(result.Decision),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }

    /// <summary>The before/after screenshots of the UI change, or { status: "None" } when there is nothing to show.</summary>
    [HttpGet("{id:guid}/visual", Name = nameof(GetExecutionVisual))]
    public async Task<IActionResult> GetExecutionVisual(
        [FromRoute] Guid id,
        [FromServices] DevPilot.Application.Executions.Ports.IExecutionRepository executionRepository,
        [FromServices] DevPilot.Application.Executions.Ports.IVisualArtifactReader visualReader,
        CancellationToken cancellationToken)
    {
        var execution = await executionRepository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            return NotFound(new { error = "Execution not found." });
        }

        var manifest = string.IsNullOrWhiteSpace(execution.WorkspacePath)
            ? null
            : await visualReader.GetManifestAsync(execution.WorkspacePath, id, cancellationToken).ConfigureAwait(false);

        return Ok(manifest ?? new DevPilot.Application.Executions.Models.VisualCaptureManifest { Status = "None" });
    }

    [HttpGet("{id:guid}/visual/{fileName}", Name = nameof(GetExecutionVisualImage))]
    public async Task<IActionResult> GetExecutionVisualImage(
        [FromRoute] Guid id,
        [FromRoute] string fileName,
        [FromServices] DevPilot.Application.Executions.Ports.IExecutionRepository executionRepository,
        [FromServices] DevPilot.Application.Executions.Ports.IVisualArtifactReader visualReader,
        CancellationToken cancellationToken)
    {
        var execution = await executionRepository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (execution is null || string.IsNullOrWhiteSpace(execution.WorkspacePath))
        {
            return NotFound(new { error = "Screenshot not found." });
        }

        var path = visualReader.ResolveImagePath(execution.WorkspacePath, id, fileName);
        if (path is null)
        {
            return NotFound(new { error = "Screenshot not found." });
        }

        Response.Headers.CacheControl = "private, max-age=60";
        return PhysicalFile(path, "image/png");
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

    /// <summary>
    /// "Request changes": the feedback is applied by the AI on this execution's own branch, build and test run again,
    /// the old approval is removed and the result returns to review. The same pull request is updated on approval.
    /// </summary>
    [HttpPost("{id:guid}/review/request-changes", Name = nameof(RequestExecutionChanges))]
    public async Task<IActionResult> RequestExecutionChanges(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        [FromBody] RequestExecutionChangesRequest? request,
        [FromServices] IRequestExecutionChangesCommandHandler changesHandler,
        CancellationToken cancellationToken)
    {
        var result = await changesHandler
            .RequestAsync(new RequestExecutionChangesCommand(id, request?.Feedback, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            RequestExecutionChangesResultStatus.BadRequest => BadRequest(new { error = result.ErrorMessage ?? "Invalid change request." }),
            RequestExecutionChangesResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            RequestExecutionChangesResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Changes cannot be requested for this execution." }),
            RequestExecutionChangesResultStatus.Failed => StatusCode(500, new { error = result.ErrorMessage ?? "Changes could not be started." }),
            RequestExecutionChangesResultStatus.Accepted => Accepted(new { message = "Changes requested.", revisionNumber = result.RevisionNumber }),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }

    /// <summary>
    /// Resumes a revision that was interrupted (for example by a restart): verification and repair run again on the
    /// existing worktree, the feedback is not applied again and no code is regenerated.
    /// </summary>
    [HttpPost("{id:guid}/review/resume-revision", Name = nameof(ResumeExecutionRevision))]
    public async Task<IActionResult> ResumeExecutionRevision(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        [FromServices] IRequestExecutionChangesCommandHandler changesHandler,
        CancellationToken cancellationToken)
    {
        var result = await changesHandler
            .ResumeAsync(id, repositoryWorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            RequestExecutionChangesResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            RequestExecutionChangesResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "The revision cannot be resumed." }),
            RequestExecutionChangesResultStatus.Failed => StatusCode(500, new { error = result.ErrorMessage ?? "The revision could not be resumed." }),
            RequestExecutionChangesResultStatus.Accepted => Accepted(new { message = "Revision resumed.", revisionNumber = result.RevisionNumber }),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }

    /// <summary>The diff of just the latest requested fix.</summary>
    [HttpGet("{id:guid}/revision/diff", Name = nameof(GetExecutionRevisionDiff))]
    public async Task<IActionResult> GetExecutionRevisionDiff(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        [FromServices] IGetExecutionRevisionDiffQueryHandler diffHandler,
        [FromQuery] int? number,
        CancellationToken cancellationToken)
    {
        var result = await diffHandler
            .HandleAsync(new GetExecutionRevisionDiffQuery(id, repositoryWorkspaceId, number), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            GetExecutionRevisionDiffStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            GetExecutionRevisionDiffStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "No fix diff available." }),
            GetExecutionRevisionDiffStatus.Success => Ok(result.Diff),
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

    [HttpPost("{id:guid}/cancel", Name = nameof(CancelExecution))]
    public async Task<IActionResult> CancelExecution(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        [FromServices] DevPilot.Application.Executions.Commands.CancelExecution.ICancelExecutionCommandHandler cancelHandler,
        CancellationToken cancellationToken)
    {
        var result = await cancelHandler
            .HandleAsync(new DevPilot.Application.Executions.Commands.CancelExecution.CancelExecutionCommand(id, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            DevPilot.Application.Executions.Commands.CancelExecution.CancelExecutionResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            DevPilot.Application.Executions.Commands.CancelExecution.CancelExecutionResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution cannot be cancelled." }),
            DevPilot.Application.Executions.Commands.CancelExecution.CancelExecutionResultStatus.Success => Ok(new { message = "Cancellation requested successfully." }),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }

    [HttpPost("{id:guid}/verify", Name = nameof(VerifyExecution))]
    public async Task<IActionResult> VerifyExecution(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        [FromServices] DevPilot.Application.Executions.Commands.VerifyExecution.IVerifyExecutionCommandHandler verifyHandler,
        CancellationToken cancellationToken)
    {
        var result = await verifyHandler
            .RequestAsync(new DevPilot.Application.Executions.Commands.VerifyExecution.VerifyExecutionCommand(id, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            DevPilot.Application.Executions.Commands.VerifyExecution.VerifyExecutionResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            DevPilot.Application.Executions.Commands.VerifyExecution.VerifyExecutionResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution cannot be verified." }),
            DevPilot.Application.Executions.Commands.VerifyExecution.VerifyExecutionResultStatus.Failed => StatusCode(500, new { error = result.ErrorMessage ?? "Verification could not be started." }),
            DevPilot.Application.Executions.Commands.VerifyExecution.VerifyExecutionResultStatus.Accepted => Accepted(new { message = "Verification started." }),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }
}
