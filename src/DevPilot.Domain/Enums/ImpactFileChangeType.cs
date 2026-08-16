using System.Text.Json.Serialization;

namespace DevPilot.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImpactFileChangeType
{
    Unknown,
    Add,
    Modify,
    Delete,
    Refactor,
}
