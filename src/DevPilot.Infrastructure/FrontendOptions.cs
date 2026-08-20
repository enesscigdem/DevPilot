namespace DevPilot.Infrastructure;

public sealed class FrontendOptions
{
    public const string SectionName = "Frontend";

    public string BaseUrl { get; set; } = "http://localhost:3000";
}
