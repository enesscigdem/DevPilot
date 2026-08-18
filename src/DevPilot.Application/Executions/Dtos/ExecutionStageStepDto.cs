using System.Text.Json.Serialization;

namespace DevPilot.Application.Executions.Dtos;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionStageStepState
{
    Todo,
    Active,
    Done,
    Failed,
    Blocked
}

public sealed class ExecutionStageStepDto
{
    public string StageKey { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public ExecutionStageStepState State { get; set; } = ExecutionStageStepState.Todo;
}
