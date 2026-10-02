using System.Diagnostics;
using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using DevPilot.Application.AiProviders;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.AiProviders;

/// <summary>
/// Claude through the official Anthropic SDK (Messages API, streamed). The SDK owns retries and
/// backoff; this class maps its outcome onto <see cref="AiResponse"/>.
/// </summary>
internal sealed class AnthropicAiProvider : IAiProvider
{
    // Claude requires an explicit output cap on every request.
    private const int FallbackMaxTokens = 16384;

    private readonly AiEndpointSettings _settings;
    private readonly ILogger _logger;

    public AnthropicAiProvider(AiEndpointSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string ProviderName => _settings.ProviderName;

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var model = !string.IsNullOrWhiteSpace(request.Model) ? request.Model : _settings.Model;

        if (string.IsNullOrWhiteSpace(model)
            || (_settings.RequiresApiKey && string.IsNullOrWhiteSpace(_settings.ApiKey)))
        {
            return Failure(model, stopwatch, AiFailureKind.Permanent,
                $"{_settings.DisplayName} provider is not configured. The model or API key is missing.");
        }

        var maxTokens = request.MaxTokens ?? _settings.MaxOutputTokens ?? FallbackMaxTokens;

        try
        {
            IAnthropicClient client = new AnthropicClient
            {
                ApiKey = _settings.ApiKey,
                MaxRetries = Math.Max(0, _settings.MaxAttempts - 1),
                Timeout = TimeSpan.FromMinutes(5),
            };
            if (!string.IsNullOrWhiteSpace(_settings.BaseUrl))
            {
                client = client.WithOptions(o => o with { BaseUrl = _settings.BaseUrl.TrimEnd('/') });
            }

            var parameters = new MessageCreateParams
            {
                Model = model,
                MaxTokens = maxTokens,
                Messages = [new() { Role = Role.User, Content = request.UserPrompt }],
            };

            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                parameters = parameters with { System = request.SystemPrompt };
            }

            var content = new StringBuilder();
            string? responseModel = null;
            string? stopReason = null;
            long? inputTokens = null;
            long? outputTokens = null;

            // Streaming keeps the connection alive for long generations (proxies drop idle requests).
            await foreach (var streamEvent in client.Messages.CreateStreaming(parameters).WithCancellation(cancellationToken))
            {
                if (streamEvent.TryPickStart(out var start))
                {
                    responseModel = start.Message.Model;
                    inputTokens = start.Message.Usage.InputTokens;
                }
                else if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    content.Append(text.Text);
                }
                else if (streamEvent.TryPickDelta(out var messageDelta))
                {
                    stopReason = messageDelta.Delta.StopReason?.Raw();
                    outputTokens = messageDelta.Usage.OutputTokens;
                }
            }

            stopwatch.Stop();
            var response = new AiResponse
            {
                Provider = ProviderName,
                Model = string.IsNullOrWhiteSpace(responseModel) ? model : responseModel,
                Content = content.ToString(),
                InputTokens = (int?)inputTokens,
                OutputTokens = (int?)outputTokens,
                Duration = stopwatch.Elapsed,
                StatusCode = 200,
                FinishReason = stopReason,
            };

            if (IsStopReason(stopReason, "maxtokens"))
            {
                response.IsSuccess = false;
                response.FailureKind = AiFailureKind.TokenLimitExceeded;
                response.ErrorMessage = "AI response exhausted the configured output token limit before producing a complete result.";
                response.FinishReason = "length";
                return response;
            }

            if (IsStopReason(stopReason, "refusal"))
            {
                response.IsSuccess = false;
                response.FailureKind = AiFailureKind.Permanent;
                response.ErrorMessage = $"{_settings.DisplayName} declined the request (safety refusal).";
                return response;
            }

            if (content.Length == 0)
            {
                response.IsSuccess = false;
                response.FailureKind = AiFailureKind.TransientServiceUnavailable;
                response.ErrorMessage = $"{_settings.DisplayName} returned empty content.";
                return response;
            }

            response.IsSuccess = true;
            response.FailureKind = AiFailureKind.None;
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(model, stopwatch, AiFailureKind.Cancelled, $"{_settings.DisplayName} request was cancelled.");
        }
        catch (AnthropicRateLimitException ex)
        {
            return Failure(model, stopwatch, AiFailureKind.RateLimited, $"{_settings.DisplayName} rate limited: {Short(ex)}", 429);
        }
        catch (Anthropic5xxException ex)
        {
            // 500/502/503/529 (overloaded) are transient on Anthropic's side.
            return Failure(model, stopwatch, AiFailureKind.TransientServiceUnavailable, $"{_settings.DisplayName} service unavailable: {Short(ex)}", 503);
        }
        catch (AnthropicApiException ex)
        {
            _logger.LogWarning("Anthropic API call failed: {Message}", Short(ex));
            return Failure(model, stopwatch, AiFailureKind.Permanent, $"{_settings.DisplayName} request failed: {Short(ex)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or TimeoutException)
        {
            return Failure(model, stopwatch, AiFailureKind.TimeoutOrConnection, $"{_settings.DisplayName} network error: {Short(ex)}");
        }
    }

    private AiResponse Failure(string model, Stopwatch stopwatch, AiFailureKind kind, string message, int? statusCode = null)
    {
        stopwatch.Stop();
        return new AiResponse
        {
            Provider = ProviderName,
            Model = model,
            Duration = stopwatch.Elapsed,
            IsSuccess = false,
            StatusCode = statusCode,
            FailureKind = kind,
            ErrorMessage = message,
        };
    }

    // The SDK enum may print as "MaxTokens" or "max_tokens"; compare without case or underscores.
    private static bool IsStopReason(string? actual, string expected) =>
        actual is not null && actual.Replace("_", string.Empty).Equals(expected, StringComparison.OrdinalIgnoreCase);

    // Exception text can echo request details; keep it short and single-line for logs and the UI.
    private static string Short(Exception ex)
    {
        var text = ex.Message.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 300 ? text : text[..300] + "...";
    }
}
