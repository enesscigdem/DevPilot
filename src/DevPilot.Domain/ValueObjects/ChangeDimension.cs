using DevPilot.Domain.Enums;

namespace DevPilot.Domain.ValueObjects;

public static class ChangeDimensionArea
{
    public const string Code = "CODE";
    public const string Api = "API";
    public const string Data = "DATA";
    public const string Tests = "TESTS";
    public const string Runtime = "RUNTIME";
    public const string Dependencies = "DEPENDENCIES";
    public const string Infrastructure = "INFRASTRUCTURE";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Code,
        Api,
        Data,
        Tests,
        Runtime,
        Dependencies,
        Infrastructure
    };

    public static bool IsValid(string area)
    {
        return All.Contains(area, StringComparer.OrdinalIgnoreCase);
    }

    public static string Normalize(string area)
    {
        if (string.IsNullOrWhiteSpace(area)) return Code;
        var trimmed = area.Trim();
        var match = All.FirstOrDefault(a => string.Equals(a, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        if (trimmed.Contains("API", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("ENDPOINT", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("CONTROLLER", StringComparison.OrdinalIgnoreCase))
            return Api;
        if (trimmed.Contains("DATA", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("DATABASE", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("PERSISTENCE", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("SCHEMA", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("MIGRATION", StringComparison.OrdinalIgnoreCase))
            return Data;
        if (trimmed.Contains("TEST", StringComparison.OrdinalIgnoreCase))
            return Tests;
        if (trimmed.Contains("RUNTIME", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("CONCURRENCY", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("PERFORMANCE", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("THREAD", StringComparison.OrdinalIgnoreCase))
            return Runtime;
        if (trimmed.Contains("DEPENDENC", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("PACKAGE", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("PROJECT REF", StringComparison.OrdinalIgnoreCase))
            return Dependencies;
        if (trimmed.Contains("INFRA", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("DOCKER", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("CI", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("DEPLOY", StringComparison.OrdinalIgnoreCase))
            return Infrastructure;

        return Code;
    }
}

public sealed class ChangeDimensionImpact
{
    public string Area { get; set; } = string.Empty;

    public SystemImpactLevel ImpactLevel { get; set; } = SystemImpactLevel.Low;

    public string Summary { get; set; } = string.Empty;

    public List<string> Details { get; set; } = new();

    public List<string> Evidence { get; set; } = new();
}
