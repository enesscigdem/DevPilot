using DevPilot.Application.AiProviders;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.AiProviders;

/// <summary>
/// The <see cref="IAiProvider"/> the rest of the app talks to. Picks the model the user assigned to
/// the request's stage (or the default model) and delegates to the matching adapter. With no models
/// configured it falls back to the legacy <c>appsettings</c> provider, so existing setups keep working.
/// </summary>
/// <remarks>
/// The model list is read once per scope (one execution or HTTP request). Callers fan out parallel
/// requests on a single scoped instance, so loading is guarded and never touches the DbContext twice.
/// </remarks>
internal sealed class RoutingAiProvider : IAiProvider
{
    private readonly DevPilotDbContext _db;
    private readonly IAiKeyProtector _keyProtector;
    private readonly IAiProviderFactory _factory;
    private readonly IAiProvider _legacyProvider;
    private readonly IAiExecutionContext? _executionContext;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly HashSet<string> _announced = new();
    private Snapshot? _snapshot;

    public RoutingAiProvider(
        DevPilotDbContext db,
        IAiKeyProtector keyProtector,
        IAiProviderFactory factory,
        IAiProvider legacyProvider,
        IAiExecutionContext? executionContext = null)
    {
        _executionContext = executionContext;
        _db = db;
        _keyProtector = keyProtector;
        _factory = factory;
        _legacyProvider = legacyProvider;
    }

    public string ProviderName => "Router";

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var pinnedId = _executionContext?.PinnedModelId;
        // Timeouts and retries of this call are reported to the execution that is running it.
        request.OnAttempt ??= _executionContext?.AttemptObserver;

        var (config, source, fallbackReason) = pinnedId is { } pinned
            ? (snapshot.GetEnabled(pinned), "Pinned", (string?)null)
            : snapshot.Resolve(request.Stage);

        if (pinnedId is not null && config is null)
        {
            // Never fall back to another model: a comparison run must be attributable to exactly one.
            return new AiResponse
            {
                Provider = "Router",
                Model = request.Model,
                IsSuccess = false,
                FailureKind = AiFailureKind.Permanent,
                ErrorMessage = "The model pinned to this execution was deleted or disabled.",
            };
        }

        if (config is null)
        {
            var legacy = await _legacyProvider.SendAsync(request, cancellationToken).ConfigureAwait(false);
            legacy.RoutingSource = "Legacy";
            legacy.FallbackReason = fallbackReason is null
                ? $"No model is configured in Settings > Models; the appsettings provider ({_legacyProvider.ProviderName}) answered."
                : $"{fallbackReason} The appsettings provider ({_legacyProvider.ProviderName}) answered.";
            return legacy;
        }

        var apiKey = snapshot.GetApiKey(config.Id);
        var provider = _factory.Create(config, apiKey);
        if (provider is null)
        {
            return Failure(config, $"The {config.AdapterType} adapter is not available yet. Pick an OpenAI-compatible model.");
        }

        if (_factory.RequiresApiKey(config) && string.IsNullOrEmpty(apiKey))
        {
            return Failure(config, $"No usable API key is stored for model {config.Name}. Open Settings > Models and enter the key again.");
        }

        await AnnounceAsync(request, config, source, fallbackReason).ConfigureAwait(false);

        // A pinned model is never replaced: a comparison run must be attributable to exactly one model.
        var alternate = pinnedId is null ? FindAlternate(snapshot, config) : null;

        var routed = new AiRequest
        {
            // The assigned model always wins over whatever name the caller put in the request.
            Model = config.ModelName,
            Stage = request.Stage,
            SystemPrompt = request.SystemPrompt,
            UserPrompt = request.UserPrompt,
            MaxTokens = CapTokens(request.MaxTokens, config.MaxOutputTokens),
            ReasoningEffort = request.ReasoningEffort,
            OnAttempt = request.OnAttempt,
            // With a model ready to take over, do not sit through every retry of one that is being rate limited.
            MaxAttempts = alternate is null ? request.MaxAttempts : PrimaryAttemptsWhenAnAlternateExists,
        };

        var response = await provider.SendAsync(routed, cancellationToken).ConfigureAwait(false);
        response.ModelConfigId = config.Id;
        response.ModelConfigName = config.Name;
        response.RoutingSource = source;
        response.FallbackReason = fallbackReason;

        if (alternate is not null && IsWorthFallingBack(response))
        {
            var answered = await TryAlternateAsync(request, config, response, alternate, snapshot, cancellationToken).ConfigureAwait(false);
            if (answered is not null)
            {
                return answered;
            }
        }

        return response;
    }

    /// <summary>Attempts the assigned model gets when another model can take over; the alternate gets the provider's full retries.</summary>
    internal const int PrimaryAttemptsWhenAnAlternateExists = 2;

    private static bool IsWorthFallingBack(AiResponse response) =>
        !response.IsSuccess &&
        response.FailureKind is AiFailureKind.RateLimited
            or AiFailureKind.TransientServiceUnavailable
            or AiFailureKind.TimeoutOrConnection;

    /// <summary>
    /// The model that takes over when the assigned one is rate limited or unavailable: the default model, otherwise the
    /// first other enabled model with a usable key. Null when there is none, and the assigned model is then retried as before.
    /// </summary>
    private AiModelConfig? FindAlternate(Snapshot snapshot, AiModelConfig primary)
    {
        foreach (var candidate in snapshot.AlternatesFor(primary.Id))
        {
            if (_factory.RequiresApiKey(candidate) && string.IsNullOrEmpty(snapshot.GetApiKey(candidate.Id)))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private async Task<AiResponse?> TryAlternateAsync(
        AiRequest request,
        AiModelConfig failed,
        AiResponse failure,
        AiModelConfig alternate,
        Snapshot snapshot,
        CancellationToken cancellationToken)
    {
        var provider = _factory.Create(alternate, snapshot.GetApiKey(alternate.Id));
        if (provider is null)
        {
            return null;
        }

        var reason = $"{failed.Name} was {DescribeFailure(failure.FailureKind)}; {alternate.Name} answered instead.";
        if (request.OnAttempt is { } observer)
        {
            try
            {
                await observer(new AiAttemptEvent(
                    1, 1, "ModelFallback", 0, false,
                    Detail: reason,
                    Model: $"{alternate.Name} ({alternate.ModelName})")).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Telemetry only.
            }
        }

        var routed = new AiRequest
        {
            Model = alternate.ModelName,
            Stage = request.Stage,
            SystemPrompt = request.SystemPrompt,
            UserPrompt = request.UserPrompt,
            MaxTokens = CapTokens(request.MaxTokens, alternate.MaxOutputTokens),
            ReasoningEffort = request.ReasoningEffort,
            OnAttempt = request.OnAttempt,
        };

        var response = await provider.SendAsync(routed, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            // Both models failed: report the assigned model's own failure, which is the meaningful one.
            failure.FallbackReason = $"{reason.Replace("answered instead", "also failed")}";
            return failure;
        }

        response.ModelConfigId = alternate.Id;
        response.ModelConfigName = alternate.Name;
        response.RoutingSource = "RuntimeFallback";
        response.FallbackReason = reason;
        return response;
    }

    private static string DescribeFailure(AiFailureKind kind) => kind switch
    {
        AiFailureKind.RateLimited => "rate limited",
        AiFailureKind.TimeoutOrConnection => "not answering",
        _ => "unavailable",
    };

    /// <summary>
    /// Makes the choice visible once per stage and model: which model the execution really uses and, when it is not the
    /// one assigned to the stage, why. The first model is also stored on the execution.
    /// </summary>
    private async Task AnnounceAsync(AiRequest request, AiModelConfig config, string source, string? fallbackReason)
    {
        var stage = request.Stage?.ToString() ?? "unspecified stage";
        bool first;
        lock (_announced)
        {
            first = _announced.Add($"{stage}|{config.Id}");
        }

        if (!first)
        {
            return;
        }

        var label = $"{config.Name} ({config.ModelName})";
        var how = source switch
        {
            "Pinned" => "pinned model",
            "StageAssignment" => "stage assignment",
            "Default" => "default model",
            "OnlyModel" => "the only configured model",
            _ => source,
        };

        if (request.OnAttempt is { } observer)
        {
            try
            {
                await observer(new AiAttemptEvent(1, 1, "ModelSelected", 0, false, Detail: $"{stage} via {how}", Model: label)).ConfigureAwait(false);
                if (fallbackReason is not null)
                {
                    await observer(new AiAttemptEvent(1, 1, "ModelFallback", 0, false, Detail: fallbackReason, Model: label)).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Telemetry only.
            }
        }

        if (_executionContext?.ExecutionId is { } executionId)
        {
            // The scoped DbContext is shared by parallel calls, so the write takes the same lock as the model load.
            await _loadLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _db.TaskExecutions
                    .Where(e => e.Id == executionId && (e.Model == null || e.Model == string.Empty))
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.Model, config.ModelName))
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Recording the model must not fail the AI call.
            }
            finally
            {
                _loadLock.Release();
            }
        }
    }

    private static int? CapTokens(int? requested, int? modelLimit) =>
        (requested, modelLimit) switch
        {
            (int r, int l) => Math.Min(r, l),
            (int r, null) => r,
            (null, int l) => l,
            _ => null,
        };

    private static AiResponse Failure(AiModelConfig config, string message) =>
        new()
        {
            Provider = config.Name,
            Model = config.ModelName,
            IsSuccess = false,
            FailureKind = AiFailureKind.Permanent,
            ErrorMessage = message,
        };

    private async Task<Snapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_snapshot is not null)
        {
            return _snapshot;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_snapshot is not null)
            {
                return _snapshot;
            }

            var models = await _db.AiModelConfigs.AsNoTracking()
                .Where(m => m.IsEnabled)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var assignments = await _db.AiStageAssignments.AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var keys = models
                .Where(m => !string.IsNullOrEmpty(m.ProtectedApiKey))
                .ToDictionary(m => m.Id, m => _keyProtector.Unprotect(m.ProtectedApiKey!));

            _snapshot = new Snapshot(models, assignments, keys);
            return _snapshot;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private sealed class Snapshot
    {
        private readonly Dictionary<Guid, AiModelConfig> _models;
        private readonly Dictionary<AiStage, Guid> _assignments;
        private readonly Dictionary<Guid, string?> _keys;
        private readonly AiModelConfig? _default;

        public Snapshot(
            IReadOnlyList<AiModelConfig> models,
            IReadOnlyList<AiStageAssignment> assignments,
            Dictionary<Guid, string?> keys)
        {
            _models = models.ToDictionary(m => m.Id);
            _assignments = assignments.ToDictionary(a => a.Stage, a => a.AiModelConfigId);
            _keys = keys;
            _default = models.FirstOrDefault(m => m.IsDefault);
        }

        /// <summary>
        /// The model for a stage, how it was chosen and, when it is not the model assigned to the stage, why.
        /// </summary>
        public (AiModelConfig? Config, string Source, string? FallbackReason) Resolve(AiStage? stage)
        {
            string? reason = null;
            if (stage is { } s)
            {
                if (_assignments.TryGetValue(s, out var modelId))
                {
                    if (_models.TryGetValue(modelId, out var assigned))
                    {
                        return (assigned, "StageAssignment", null);
                    }

                    reason = $"The model assigned to {s} is disabled or deleted, so another model was used.";
                }
                else
                {
                    reason = $"No model is assigned to {s}, so the default model was used.";
                }
            }

            if (_default is not null)
            {
                return (_default, "Default", reason);
            }

            // No default flagged but models exist: use the only one rather than silently ignoring the user's setup.
            return _models.Count == 1
                ? (_models.Values.First(), "OnlyModel", reason)
                : (null, "Legacy", reason);
        }

        /// <summary>
        /// Other enabled models in the order they take over: the default model, then the model the user assigned to code
        /// generation (their workhorse, so a sensible and affordable choice), then the rest by name.
        /// </summary>
        public IEnumerable<AiModelConfig> AlternatesFor(Guid modelId)
        {
            var workhorseId = _assignments.GetValueOrDefault(AiStage.CodeGeneration);
            return _models.Values
                .Where(m => m.Id != modelId)
                .OrderByDescending(m => m.IsDefault)
                .ThenByDescending(m => m.Id == workhorseId)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
        }

        public AiModelConfig? GetEnabled(Guid modelId) => _models.GetValueOrDefault(modelId);

        public string? GetApiKey(Guid modelId) => _keys.GetValueOrDefault(modelId);
    }
}
