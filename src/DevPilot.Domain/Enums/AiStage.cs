using System.Text.Json.Serialization;

namespace DevPilot.Domain.Enums;

/// <summary>A pipeline step that can be routed to its own model.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiStage
{
    /// <summary>Task impact analysis and planning.</summary>
    Planning = 0,

    /// <summary>Generating new or modified files.</summary>
    CodeGeneration = 1,

    /// <summary>Repairing compile and test failures.</summary>
    Repair = 2,

    /// <summary>Project Brain question answering.</summary>
    Brain = 3,
}
