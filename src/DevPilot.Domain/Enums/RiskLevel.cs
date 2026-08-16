using System.Text.Json.Serialization;

namespace DevPilot.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical,
}
