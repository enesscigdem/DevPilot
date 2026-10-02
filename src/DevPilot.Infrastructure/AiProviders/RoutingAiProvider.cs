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
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private Snapshot? _snapshot;

    public RoutingAiProvider(
        DevPilotDbContext db,
        IAiKeyProtector keyProtector,
        IAiProviderFactory factory,
        IAiProvider legacyProvider)
    {
        _db = db;
        _keyProtector = keyProtector;
        _factory = factory;
        _legacyProvider = legacyProvider;
    }

    public string ProviderName => "Router";

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var config = snapshot.Resolve(request.Stage);

        if (config is null)
        {
            return await _legacyProvider.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

        var routed = new AiRequest
        {
            // The assigned model always wins over whatever name the caller put in the request.
            Model = config.ModelName,
            Stage = request.Stage,
            SystemPrompt = request.SystemPrompt,
            UserPrompt = request.UserPrompt,
            MaxTokens = CapTokens(request.MaxTokens, config.MaxOutputTokens),
            ReasoningEffort = request.ReasoningEffort,
        };

        return await provider.SendAsync(routed, cancellationToken).ConfigureAwait(false);
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

        public AiModelConfig? Resolve(AiStage? stage)
        {
            if (stage is { } s
                && _assignments.TryGetValue(s, out var modelId)
                && _models.TryGetValue(modelId, out var assigned))
            {
                return assigned;
            }

            // No default flagged but models exist: use the only one rather than silently ignoring the user's setup.
            return _default ?? (_models.Count == 1 ? _models.Values.First() : null);
        }

        public string? GetApiKey(Guid modelId) => _keys.GetValueOrDefault(modelId);
    }
}
