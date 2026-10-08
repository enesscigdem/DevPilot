using DevPilot.Application.Tasks.Commands.CreateTask;
using DevPilot.Application.Tasks.Dtos;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Trackers;

public sealed record ImportTrackerIssuesCommand(Guid ConnectionId, Guid RepositoryWorkspaceId, IReadOnlyList<string> IssueKeys);

public enum ImportOutcome
{
    Imported,
    AlreadyImported,
    Failed,
}

public sealed record ImportedIssueResult(string Key, ImportOutcome Outcome, TaskDto? Task, string? Message);

public sealed class ImportTrackerIssuesResult
{
    public bool Success { get; set; }

    /// <summary>Set when the whole request was rejected; per-issue outcomes are in <see cref="Items"/>.</summary>
    public string? ErrorMessage { get; set; }

    public IReadOnlyList<ImportedIssueResult> Items { get; set; } = Array.Empty<ImportedIssueResult>();
}

public interface IImportTrackerIssuesCommandHandler
{
    Task<ImportTrackerIssuesResult> HandleAsync(ImportTrackerIssuesCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns tracker issues into tasks through the handler every task goes through, so no rule is bypassed. An issue that
/// is already a task in the workspace is skipped, and one that fails never stops the others.
/// </summary>
public sealed class ImportTrackerIssuesCommandHandler : IImportTrackerIssuesCommandHandler
{
    public const int MaxIssuesPerImport = 50;
    public const string JiraSource = "Jira";

    private readonly ITrackerConnectionStore _connections;
    private readonly ITrackerClient _client;
    private readonly ITaskRepository _tasks;
    private readonly ICreateTaskCommandHandler _createTask;
    private readonly ILogger<ImportTrackerIssuesCommandHandler> _logger;

    public ImportTrackerIssuesCommandHandler(
        ITrackerConnectionStore connections,
        ITrackerClient client,
        ITaskRepository tasks,
        ICreateTaskCommandHandler createTask,
        ILogger<ImportTrackerIssuesCommandHandler> logger)
    {
        _connections = connections;
        _client = client;
        _tasks = tasks;
        _createTask = createTask;
        _logger = logger;
    }

    public async Task<ImportTrackerIssuesResult> HandleAsync(
        ImportTrackerIssuesCommand command,
        CancellationToken cancellationToken = default)
    {
        var keys = command.IssueKeys
            .Select(k => k?.Trim().ToUpperInvariant() ?? string.Empty)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (keys.Count == 0)
        {
            return Rejected("Select at least one issue to import.");
        }

        if (keys.Count > MaxIssuesPerImport)
        {
            return Rejected($"At most {MaxIssuesPerImport} issues can be imported at once.");
        }

        var connection = await _connections.GetInfoAsync(command.ConnectionId, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return Rejected("The tracker connection was not found or its token can no longer be read. Add the connection again.");
        }

        var existing = await _tasks
            .FindByExternalKeysAsync(command.RepositoryWorkspaceId, JiraSource, keys, cancellationToken)
            .ConfigureAwait(false);

        var items = new List<ImportedIssueResult>(keys.Count);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existing.Contains(key))
            {
                items.Add(new ImportedIssueResult(key, ImportOutcome.AlreadyImported, null, "Already a task in this repository."));
                continue;
            }

            items.Add(await ImportOneAsync(connection, command.RepositoryWorkspaceId, key, cancellationToken).ConfigureAwait(false));
        }

        return new ImportTrackerIssuesResult { Success = true, Items = items };
    }

    private async Task<ImportedIssueResult> ImportOneAsync(
        TrackerConnectionInfo connection,
        Guid workspaceId,
        string key,
        CancellationToken cancellationToken)
    {
        var fetched = await _client.GetIssueAsync(connection, key, cancellationToken).ConfigureAwait(false);
        if (!fetched.IsSuccess || fetched.Data is null)
        {
            return new ImportedIssueResult(key, ImportOutcome.Failed, null, fetched.ErrorMessage ?? "The issue could not be read.");
        }

        var issue = fetched.Data;
        var created = await _createTask.HandleAsync(
            new CreateTaskCommand(new CreateTaskDto
            {
                RepositoryWorkspaceId = workspaceId,
                Title = Truncate(issue.Summary, 200),
                Description = Truncate(issue.Description ?? string.Empty, 10000),
                Priority = MapPriority(issue.Priority),
                ExternalSource = JiraSource,
                ExternalKey = issue.Key.ToUpperInvariant(),
                ExternalUrl = issue.Url,
                ExternalConnectionId = connection.Id,
            }),
            cancellationToken).ConfigureAwait(false);

        if (!created.Success)
        {
            return new ImportedIssueResult(key, ImportOutcome.Failed, null, created.ErrorMessage ?? "The task could not be created.");
        }

        _logger.LogInformation("Imported {Key} as task {TaskId}.", issue.Key, created.Task?.Id);
        return new ImportedIssueResult(key, ImportOutcome.Imported, created.Task, null);
    }

    public static DevelopmentTaskPriority MapPriority(string? trackerPriority) =>
        trackerPriority?.Trim().ToLowerInvariant() switch
        {
            "highest" or "blocker" or "critical" or "urgent" => DevelopmentTaskPriority.Critical,
            "high" or "major" => DevelopmentTaskPriority.High,
            "low" or "minor" or "lowest" or "trivial" => DevelopmentTaskPriority.Low,
            _ => DevelopmentTaskPriority.Medium,
        };

    private static string Truncate(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..(max - 1)].TrimEnd() + "…";
    }

    private static ImportTrackerIssuesResult Rejected(string message) =>
        new() { Success = false, ErrorMessage = message };
}
