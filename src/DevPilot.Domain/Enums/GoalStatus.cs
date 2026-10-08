using System.Text.Json.Serialization;

namespace DevPilot.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GoalStatus
{
    /// <summary>DevPilot is still moving the goal's tasks forward.</summary>
    Active = 0,

    /// <summary>Every task has reached an end: merged, failed or stopped.</summary>
    Completed = 1,

    /// <summary>The person stopped the goal; tasks already running were left alone.</summary>
    Cancelled = 2
}
