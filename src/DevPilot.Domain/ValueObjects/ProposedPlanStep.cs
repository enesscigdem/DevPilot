namespace DevPilot.Domain.ValueObjects;

public sealed class ProposedPlanStep
{
    public int Order { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string> RelatedFiles { get; set; } = new();
}
