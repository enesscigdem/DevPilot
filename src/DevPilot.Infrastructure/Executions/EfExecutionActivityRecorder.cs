using System.Text.Json;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

public sealed class EfExecutionActivityRecorder : IExecutionActivityRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EfExecutionActivityRecorder> _logger;

    public EfExecutionActivityRecorder(
        IServiceScopeFactory scopeFactory,
        ILogger<EfExecutionActivityRecorder> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RecordActivityAsync(
        Guid executionId,
        ExecutionStage stage,
        ExecutionActivityStatus status,
        string message,
        ExecutionActivityMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sanitizedMessage = SanitizeMessage(message);
            string? metadataJson = null;

            if (metadata != null)
            {
                // Strict allowlist serialization of ExecutionActivityMetadata
                metadataJson = JsonSerializer.Serialize(metadata, JsonOptions);
            }

            var activity = new ExecutionActivity
            {
                Id = Guid.NewGuid(),
                ExecutionId = executionId,
                Stage = stage,
                Status = status,
                CreatedAt = DateTime.UtcNow,
                Message = sanitizedMessage,
                MetadataJson = metadataJson
            };

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DevPilotDbContext>();

            dbContext.ExecutionActivities.Add(activity);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Respect caller cancellation
            throw;
        }
        catch (Exception ex)
        {
            // Telemetry failure is non-fatal and must NEVER disrupt the execution pipeline
            _logger.LogWarning(
                ex,
                "EfExecutionActivityRecorder: failed to persist activity for execution {ExecutionId} (Stage={Stage}, Status={Status}).",
                executionId,
                stage,
                status);
        }
    }

    public static string SanitizeMessage(string rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return "Activity status updated.";
        }

        // Take only the first non-empty line to strip multiline stack traces / outputs
        var firstLine = rawMessage
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? rawMessage.Trim();

        if (firstLine.Length > 500)
        {
            firstLine = firstLine[..500].TrimEnd();
        }

        return firstLine;
    }
}
