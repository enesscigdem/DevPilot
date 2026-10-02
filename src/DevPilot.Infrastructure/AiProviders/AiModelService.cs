using DevPilot.Application.AiProviders;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.AiProviders;

internal sealed class AiModelService : IAiModelService
{
    private const string TestPrompt = "Reply with the single word: ok";

    // A connection test that has not answered by now is a result in itself: waiting for the 5 minute
    // client timeout would leave the user staring at a spinner.
    private static readonly TimeSpan DefaultTestTimeout = TimeSpan.FromSeconds(45);

    private readonly DevPilotDbContext _db;
    private readonly IAiKeyProtector _keyProtector;
    private readonly IAiProviderFactory _factory;
    private readonly TimeSpan _testTimeout;

    public AiModelService(
        DevPilotDbContext db,
        IAiKeyProtector keyProtector,
        IAiProviderFactory factory,
        TimeSpan? testTimeout = null)
    {
        _db = db;
        _keyProtector = keyProtector;
        _factory = factory;
        _testTimeout = testTimeout ?? DefaultTestTimeout;
    }

    public async Task<IReadOnlyList<AiModelDto>> ListAsync(CancellationToken cancellationToken)
    {
        var models = await _db.AiModelConfigs.AsNoTracking()
            .OrderBy(m => m.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return models.Select(ToDto).ToList();
    }

    public async Task<AiModelDto> CreateAsync(SaveAiModelRequest request, CancellationToken cancellationToken)
    {
        var name = Validate(request);
        await EnsureNameIsFreeAsync(name, excludeId: null, cancellationToken).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var model = new AiModelConfig { Id = Guid.NewGuid(), CreatedAt = now };
        Apply(model, request, name, now);

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            SetApiKey(model, request.ApiKey);
        }

        // The first model becomes the default so the app starts using it right away.
        var hasAnyModel = await _db.AiModelConfigs.AnyAsync(cancellationToken).ConfigureAwait(false);
        model.IsDefault = request.IsDefault || (!hasAnyModel && model.IsEnabled);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (model.IsDefault)
        {
            await ClearDefaultAsync(model.Id, cancellationToken).ConfigureAwait(false);
        }

        _db.AiModelConfigs.Add(model);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ToDto(model);
    }

    public async Task<AiModelDto> UpdateAsync(Guid id, SaveAiModelRequest request, CancellationToken cancellationToken)
    {
        var name = Validate(request);
        var model = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        await EnsureNameIsFreeAsync(name, excludeId: id, cancellationToken).ConfigureAwait(false);

        var hasNewKey = !string.IsNullOrWhiteSpace(request.ApiKey);
        if (!hasNewKey && model.ProtectedApiKey is not null && HostChanged(model.BaseUrl, request.BaseUrl))
        {
            // Never forward a stored key to a different host.
            throw new ArgumentException("The endpoint changed. Enter the API key again so it is not sent to a different host.");
        }

        Apply(model, request, name, DateTime.UtcNow);
        model.IsDefault = request.IsDefault;
        if (hasNewKey)
        {
            SetApiKey(model, request.ApiKey!);
        }

        // Settings changed, so an earlier test result no longer describes this configuration.
        model.LastTestedAt = null;
        model.LastTestSucceeded = null;
        model.LastTestMessage = null;

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (model.IsDefault)
        {
            await ClearDefaultAsync(model.Id, cancellationToken).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ToDto(model);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var model = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        _db.AiModelConfigs.Remove(model);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiModelTestResultDto> TestAsync(Guid id, CancellationToken cancellationToken)
    {
        var model = await FindAsync(id, cancellationToken).ConfigureAwait(false);

        var apiKey = string.IsNullOrEmpty(model.ProtectedApiKey) ? null : _keyProtector.Unprotect(model.ProtectedApiKey);
        var result = await RunTestAsync(model, apiKey, cancellationToken).ConfigureAwait(false);

        model.LastTestedAt = DateTime.UtcNow;
        model.LastTestSucceeded = result.Success;
        model.LastTestMessage = Truncate(result.Message, 1000);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<IReadOnlyList<AiStageAssignmentDto>> GetStageAssignmentsAsync(CancellationToken cancellationToken)
    {
        var assignments = await _db.AiStageAssignments.AsNoTracking()
            .ToDictionaryAsync(a => a.Stage, a => a.AiModelConfigId, cancellationToken)
            .ConfigureAwait(false);

        return Enum.GetValues<AiStage>()
            .Select(stage => new AiStageAssignmentDto
            {
                Stage = stage,
                AiModelConfigId = assignments.TryGetValue(stage, out var modelId) ? modelId : null,
            })
            .ToList();
    }

    public async Task<IReadOnlyList<AiStageAssignmentDto>> SetStageAssignmentsAsync(
        IReadOnlyList<AiStageAssignmentDto> assignments,
        CancellationToken cancellationToken)
    {
        var duplicate = assignments.GroupBy(a => a.Stage).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Stage {duplicate.Key} appears more than once.");
        }

        var enabledModelIds = await _db.AiModelConfigs.AsNoTracking()
            .Where(m => m.IsEnabled)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var assignment in assignments)
        {
            if (assignment.AiModelConfigId is { } modelId && !enabledModelIds.Contains(modelId))
            {
                throw new ArgumentException($"The model assigned to {assignment.Stage} does not exist or is disabled.");
            }
        }

        var existing = await _db.AiStageAssignments.ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        foreach (var assignment in assignments)
        {
            var current = existing.FirstOrDefault(e => e.Stage == assignment.Stage);

            if (assignment.AiModelConfigId is not { } modelId)
            {
                if (current is not null)
                {
                    _db.AiStageAssignments.Remove(current);
                }

                continue;
            }

            if (current is null)
            {
                _db.AiStageAssignments.Add(new AiStageAssignment
                {
                    Id = Guid.NewGuid(),
                    Stage = assignment.Stage,
                    AiModelConfigId = modelId,
                    UpdatedAt = now,
                });
            }
            else
            {
                current.AiModelConfigId = modelId;
                current.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await GetStageAssignmentsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AiModelTestResultDto> RunTestAsync(AiModelConfig model, string? apiKey, CancellationToken cancellationToken)
    {
        if (_factory.RequiresApiKey(model) && string.IsNullOrEmpty(apiKey))
        {
            return Fail("No usable API key is stored for this model. Enter the key again.");
        }

        var provider = _factory.Create(model, apiKey, forConnectionTest: true);
        if (provider is null)
        {
            return Fail($"The {model.AdapterType} adapter is not available yet.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_testTimeout);

        var response = await provider.SendAsync(
            new AiRequest { Model = model.ModelName, UserPrompt = TestPrompt, MaxTokens = 256 },
            timeout.Token).ConfigureAwait(false);

        if (response.FailureKind == AiFailureKind.Cancelled && !cancellationToken.IsCancellationRequested)
        {
            return new AiModelTestResultDto
            {
                Success = false,
                Message = $"No answer within {(int)_testTimeout.TotalSeconds} seconds. The endpoint was reached but the model did not respond. " +
                          "Try again, or test a faster model to check the key and address.",
                DurationMs = (long)_testTimeout.TotalMilliseconds,
                Model = model.ModelName,
            };
        }

        // A reasoning model may spend the whole tiny budget thinking; the endpoint, key and model
        // name are still proven good, which is all a connection test must establish.
        var reachable = response.IsSuccess || response.FailureKind == AiFailureKind.TokenLimitExceeded;

        return new AiModelTestResultDto
        {
            Success = reachable,
            Message = reachable
                ? "Connection OK."
                : response.ErrorMessage ?? "The model did not answer.",
            DurationMs = (long)response.Duration.TotalMilliseconds,
            Model = string.IsNullOrWhiteSpace(response.Model) ? model.ModelName : response.Model,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
        };

        static AiModelTestResultDto Fail(string message) => new() { Success = false, Message = message };
    }

    private async Task<AiModelConfig> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.AiModelConfigs.FirstOrDefaultAsync(m => m.Id == id, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"AI model {id} was not found.");

    private async Task EnsureNameIsFreeAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var lowered = name.ToLowerInvariant();
        var taken = await _db.AiModelConfigs.AnyAsync(
            m => m.Id != excludeId && m.Name.ToLower() == lowered,
            cancellationToken).ConfigureAwait(false);

        if (taken)
        {
            throw new ArgumentException($"A model named '{name}' already exists.");
        }
    }

    private async Task ClearDefaultAsync(Guid exceptId, CancellationToken cancellationToken)
    {
        var others = _db.AiModelConfigs.Where(m => m.IsDefault && m.Id != exceptId);

        // Run as its own statement first: the unique "one default" index would reject a single
        // batch that sets the new default before the old one is cleared.
        if (await others.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            await others
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsDefault, false), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string Validate(SaveAiModelRequest request)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 120)
        {
            throw new ArgumentException("Name is required and must be at most 120 characters.");
        }

        if (!Enum.IsDefined(request.AdapterType))
        {
            throw new ArgumentException("Unknown adapter type.");
        }

        if (!Uri.TryCreate(request.BaseUrl?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Base URL must be an absolute http or https address.");
        }

        if (string.IsNullOrWhiteSpace(request.ModelName))
        {
            throw new ArgumentException("Model name is required.");
        }

        if (request.MaxOutputTokens is <= 0)
        {
            throw new ArgumentException("Max output tokens must be greater than zero.");
        }

        if (request.InputPricePerMillionTokensUsd is < 0 || request.OutputPricePerMillionTokensUsd is < 0)
        {
            throw new ArgumentException("Prices cannot be negative.");
        }

        if (request.IsDefault && !request.IsEnabled)
        {
            throw new ArgumentException("A disabled model cannot be the default.");
        }

        return name;
    }

    private static void Apply(AiModelConfig model, SaveAiModelRequest request, string name, DateTime now)
    {
        model.Name = name;
        model.AdapterType = request.AdapterType;
        model.BaseUrl = AiBaseUrl.Normalize(request.BaseUrl);
        model.ModelName = request.ModelName.Trim();
        model.MaxOutputTokens = request.MaxOutputTokens;
        model.SupportsReasoningEffort = request.SupportsReasoningEffort;
        model.UseMaxCompletionTokens = request.UseMaxCompletionTokens;
        model.InputPricePerMillionTokensUsd = request.InputPricePerMillionTokensUsd;
        model.OutputPricePerMillionTokensUsd = request.OutputPricePerMillionTokensUsd;
        model.IsEnabled = request.IsEnabled;
        model.UpdatedAt = now;
    }

    private void SetApiKey(AiModelConfig model, string apiKey)
    {
        var trimmed = apiKey.Trim();
        model.ProtectedApiKey = _keyProtector.Protect(trimmed);
        model.ApiKeyHint = trimmed.Length >= 8 ? "..." + trimmed[^4..] : "....";
    }

    private static bool HostChanged(string oldUrl, string newUrl)
    {
        if (!Uri.TryCreate(oldUrl, UriKind.Absolute, out var oldUri)
            || !Uri.TryCreate(newUrl?.Trim(), UriKind.Absolute, out var newUri))
        {
            return true;
        }

        return !string.Equals(oldUri.Authority, newUri.Authority, StringComparison.OrdinalIgnoreCase);
    }

    private static AiModelDto ToDto(AiModelConfig m) =>
        new()
        {
            Id = m.Id,
            Name = m.Name,
            AdapterType = m.AdapterType,
            BaseUrl = m.BaseUrl,
            ModelName = m.ModelName,
            HasApiKey = !string.IsNullOrEmpty(m.ProtectedApiKey),
            ApiKeyHint = m.ApiKeyHint,
            MaxOutputTokens = m.MaxOutputTokens,
            SupportsReasoningEffort = m.SupportsReasoningEffort,
            UseMaxCompletionTokens = m.UseMaxCompletionTokens,
            InputPricePerMillionTokensUsd = m.InputPricePerMillionTokensUsd,
            OutputPricePerMillionTokensUsd = m.OutputPricePerMillionTokensUsd,
            IsEnabled = m.IsEnabled,
            IsDefault = m.IsDefault,
            LastTestedAt = m.LastTestedAt,
            LastTestSucceeded = m.LastTestSucceeded,
            LastTestMessage = m.LastTestMessage,
        };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
