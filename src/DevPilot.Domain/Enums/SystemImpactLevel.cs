using System.Text.Json.Serialization;

namespace DevPilot.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SystemImpactLevel
{
    Low,
    Medium,
    High,
    Critical,
}
