using System.Text.Json.Serialization;

namespace DevPilot.Domain.Enums;

/// <summary>How far DevPilot may carry a task without a human, per repository workspace.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutomationLevel
{
    /// <summary>Every step is triggered by a person.</summary>
    Manual = 0,

    /// <summary>The plan is approved and the execution started automatically; review stays with a person.</summary>
    SemiAuto = 1,

    /// <summary>A change that passes every safety gate is approved, committed, pushed and opened as a pull request.</summary>
    AutoPr = 2,

    /// <summary>The pull request is also merged once CI is green.</summary>
    FullAuto = 3
}
