using DevPilot.Application.Automation;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

public sealed record AutomationPolicyDto(
    Guid RepositoryWorkspaceId,
    string Level,
    bool Paused,
    DateTime? ActiveSince,
    int MaxFilesChanged,
    int MaxLinesChanged,
    int MaxParallelExecutions,
    IReadOnlyList<string> ProtectedPaths,
    bool RequireGreenCiForMerge,
    string ConflictMode,
    bool AllowBuildOnlyDelivery,
    bool RequireVisualReview);

public sealed record UpdateAutomationPolicyRequest(
    AutomationLevel Level,
    bool Paused,
    int MaxFilesChanged,
    int MaxLinesChanged,
    int MaxParallelExecutions,
    IReadOnlyList<string>? ProtectedPaths,
    bool RequireGreenCiForMerge,
    ConflictMode? ConflictMode = null,
    bool? AllowBuildOnlyDelivery = null,
    bool? RequireVisualReview = null);

[ApiController]
[Route("api/repositoryworkspaces/{workspaceId:guid}/automation")]
[Produces("application/json")]
public class AutomationController : ControllerBase
{
    private const int MaxProtectedPatterns = 50;
    private const int MaxPatternLength = 200;

    private readonly IAutomationPolicyStore _store;

    public AutomationController(IAutomationPolicyStore store)
    {
        _store = store;
    }

    /// <summary>Returns the automation policy; a workspace that never configured one reports the Manual defaults.</summary>
    [HttpGet(Name = nameof(GetAutomationPolicy))]
    public async Task<IActionResult> GetAutomationPolicy([FromRoute] Guid workspaceId, CancellationToken cancellationToken)
    {
        var policy = await _store.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false)
            ?? new AutomationPolicy { RepositoryWorkspaceId = workspaceId };
        return Ok(ToDto(policy));
    }

    [HttpPut(Name = nameof(UpdateAutomationPolicy))]
    public async Task<IActionResult> UpdateAutomationPolicy(
        [FromRoute] Guid workspaceId,
        [FromBody] UpdateAutomationPolicyRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Level))
        {
            return BadRequest(new { error = "Unknown automation level." });
        }

        if (request.ConflictMode is { } mode && !Enum.IsDefined(mode))
        {
            return BadRequest(new { error = "Unknown conflict mode." });
        }

        if (request.MaxFilesChanged is < 1 or > 200 ||
            request.MaxLinesChanged is < 1 or > 20000 ||
            request.MaxParallelExecutions is < 1 or > 5)
        {
            return BadRequest(new { error = "Limits are out of range: files 1-200, lines 1-20000, parallel executions 1-5." });
        }

        var patterns = (request.ProtectedPaths ?? Array.Empty<string>())
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (patterns.Count > MaxProtectedPatterns || patterns.Any(p => p.Length > MaxPatternLength || p.Contains('\n')))
        {
            return BadRequest(new { error = "Too many or too long protected path patterns." });
        }

        var saved = await _store.SaveAsync(
            new AutomationPolicy
            {
                RepositoryWorkspaceId = workspaceId,
                Level = request.Level,
                Paused = request.Paused,
                MaxFilesChanged = request.MaxFilesChanged,
                MaxLinesChanged = request.MaxLinesChanged,
                MaxParallelExecutions = request.MaxParallelExecutions,
                ProtectedPaths = string.Join('\n', patterns),
                RequireGreenCiForMerge = request.RequireGreenCiForMerge,
                // A client that does not send it must not silently switch a repository to the careful mode.
                ConflictMode = request.ConflictMode ?? ConflictMode.Balanced,
                // A client that does not send these must not loosen the evidence required before automatic delivery.
                AllowBuildOnlyDelivery = request.AllowBuildOnlyDelivery ?? false,
                RequireVisualReview = request.RequireVisualReview ?? true
            },
            cancellationToken).ConfigureAwait(false);

        return saved is null ? NotFound(new { error = "Repository workspace not found." }) : Ok(ToDto(saved));
    }

    private static AutomationPolicyDto ToDto(AutomationPolicy policy) =>
        new(
            policy.RepositoryWorkspaceId,
            policy.Level.ToString(),
            policy.Paused,
            policy.ActiveSince,
            policy.MaxFilesChanged,
            policy.MaxLinesChanged,
            policy.MaxParallelExecutions,
            policy.ProtectedPaths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            policy.RequireGreenCiForMerge,
            policy.ConflictMode.ToString(),
            policy.AllowBuildOnlyDelivery,
            policy.RequireVisualReview);
}
