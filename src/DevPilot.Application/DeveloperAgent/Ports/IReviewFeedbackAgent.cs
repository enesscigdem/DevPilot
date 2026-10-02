using DevPilot.Application.DeveloperAgent.Models;

namespace DevPilot.Application.DeveloperAgent.Ports;

/// <summary>Applies a reviewer's "please fix this" feedback to a worktree that already holds the change.</summary>
public interface IReviewFeedbackAgent
{
    Task<DeveloperAgentResult> ApplyReviewFeedbackAsync(
        ReviewFeedbackRequest request,
        CancellationToken cancellationToken = default);
}
