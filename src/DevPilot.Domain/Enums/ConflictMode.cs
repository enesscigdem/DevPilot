using System.Text.Json.Serialization;

namespace DevPilot.Domain.Enums;

/// <summary>How careful a goal is about two tasks changing the same file at the same time.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConflictMode
{
    /// <summary>Two tasks that change the same file never run together: the later one waits until the earlier one is merged.</summary>
    Careful = 0,

    /// <summary>
    /// Tasks share an ordinary file freely, because git merges changes to different parts of a file by itself.
    /// They still wait when a merge is likely to fail anyway: dependency and project files, lock files,
    /// migrations and generated files, or a file one of them creates, deletes or restructures.
    /// </summary>
    Balanced = 1,

    /// <summary>Tasks never wait for each other because of shared files; a merge conflict is resolved by a person.</summary>
    Fast = 2
}
