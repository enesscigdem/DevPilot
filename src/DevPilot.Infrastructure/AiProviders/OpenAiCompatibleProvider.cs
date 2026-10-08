using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DevPilot.Application.AiProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.AiProviders;

/// <summary>
/// Talks to any OpenAI-compatible <c>/chat/completions</c> endpoint (OpenAI, Kimi, DeepSeek, Qwen,
/// Mistral, OpenRouter, Ollama, LM Studio, vLLM, ...). Everything endpoint-specific comes from
/// <see cref="OpenAiCompatibleSettings"/>, so the same retry/streaming logic serves every model.
/// </summary>
internal class OpenAiCompatibleProvider : IAiProvider
{
    public const string HttpClientName = "AiModel";

    public string ProviderName { get; }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly string _displayName;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly string? _reasoningEffort;
    private readonly int? _maxOutputTokens;
    private readonly bool _isStreaming;
    private readonly bool _isConfigured;
    private readonly bool _supportsReasoningEffort;
    private readonly bool _useMaxCompletionTokens;
    private readonly int _baseDelayMs;
    private readonly int _maxAttempts;
    private readonly int _maxRetryAfterMs;
    private readonly TimeSpan _totalTimeout;
    private readonly TimeSpan _streamIdleTimeout;

    public OpenAiCompatibleProvider(
        IHttpClientFactory httpClientFactory,
        OpenAiCompatibleSettings settings,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        ProviderName = settings.ProviderName;
        _displayName = settings.DisplayName;
        _baseUrl = settings.BaseUrl;
        _model = settings.Model;
        _apiKey = settings.ApiKey;
        _reasoningEffort = settings.ReasoningEffort;
        _maxOutputTokens = settings.MaxOutputTokens is > 0 ? settings.MaxOutputTokens : null;
        _isStreaming = settings.Stream;
        _supportsReasoningEffort = settings.SupportsReasoningEffort;
        _useMaxCompletionTokens = settings.UseMaxCompletionTokens;
        _baseDelayMs = settings.BaseDelayMs > 0 ? settings.BaseDelayMs : 1000;
        _maxAttempts = settings.MaxAttempts > 0 ? settings.MaxAttempts : 4;
        _maxRetryAfterMs = settings.MaxRetryAfterMs > 0 ? settings.MaxRetryAfterMs : 30000;
        _totalTimeout = settings.TotalTimeout;
        _streamIdleTimeout = settings.StreamIdleTimeout;

        // Local runtimes (Ollama, LM Studio) usually need no key, so the key is only required
        // when the settings say so.
        _isConfigured =
            (!settings.RequiresApiKey || !string.IsNullOrWhiteSpace(_apiKey)) &&
            !string.IsNullOrWhiteSpace(_baseUrl) &&
            !string.IsNullOrWhiteSpace(_model);
    }

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var model = !string.IsNullOrWhiteSpace(request.Model) ? request.Model : _model;
        var reasoningEffort = _supportsReasoningEffort ? request.ReasoningEffort ?? _reasoningEffort : null;
        var maxTokens = request.MaxTokens ?? _maxOutputTokens;

        if (!_isConfigured)
        {
            _logger.LogWarning(
                "{Provider} provider is not configured. The base URL, model or API key is missing.",
                _displayName);

            stopwatch.Stop();

            return new AiResponse
            {
                Provider = ProviderName,
                Model = model,
                Duration = stopwatch.Elapsed,
                IsSuccess = false,
                FailureKind = AiFailureKind.Permanent,
                ErrorMessage = $"{_displayName} provider is not configured. The base URL, model or API key is missing.",
            };
        }

        var messages = new List<Message>(2);
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new Message { Role = "system", Content = request.SystemPrompt });
        }

        messages.Add(new Message { Role = "user", Content = request.UserPrompt });

        var payload = new ChatCompletionRequest
        {
            Model = model,
            Messages = messages,
            Stream = _isStreaming,
            MaxTokens = _useMaxCompletionTokens ? null : maxTokens,
            MaxCompletionTokens = _useMaxCompletionTokens ? maxTokens : null,
            ReasoningEffort = reasoningEffort,
            StreamOptions = _isStreaming ? new StreamOptions { IncludeUsage = true } : null,
        };

        var payloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        var payloadLength = payloadJson.Length;
        var inputTokenEstimate = EstimateTokenCount(request.SystemPrompt, request.UserPrompt);

        int maxAttempts = request.MaxAttempts is > 0 ? Math.Min(request.MaxAttempts.Value, _maxAttempts) : _maxAttempts;
        int baseDelayMs = _baseDelayMs;

        int attempt = 0;
        Exception? lastException = null;

        while (attempt < maxAttempts)
        {
            attempt++;
            if (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    FailureKind = AiFailureKind.Cancelled,
                    ErrorMessage = $"{_displayName} request was cancelled.",
                };
            }
            var attemptStopwatch = Stopwatch.StartNew();

            // One deadline for the whole attempt (headers and the entire stream) and one for silence while streaming.
            // A caller cancellation is told apart from both, so it is never retried.
            using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_totalTimeout > TimeSpan.Zero)
            {
                totalCts.CancelAfter(_totalTimeout);
            }

            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
            var attemptToken = idleCts.Token;
            Action armIdleTimer = () =>
            {
                if (_streamIdleTimeout > TimeSpan.Zero)
                {
                    idleCts.CancelAfter(_streamIdleTimeout);
                }
            };

            try
            {
                using var client = _httpClientFactory.CreateClient(HttpClientName);
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, BuildCompletionUri());
                // Local runtimes (Ollama, LM Studio) run without a key; do not send an empty bearer token.
                if (!string.IsNullOrWhiteSpace(_apiKey))
                {
                    requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                }
                requestMessage.Content = new StringContent(payloadJson, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json"));

                var response = await client
                    .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, attemptToken)
                    .ConfigureAwait(false);

                attemptStopwatch.Stop();
                var requestId = GetHeaderValue(response.Headers, "x-request-id") ?? GetHeaderValue(response.Headers, "request-id");
                var retryAfterHeader = response.Headers.RetryAfter?.ToString();

                if (response.IsSuccessStatusCode)
                {
                    if (_isStreaming)
                    {
                        var streamResponse = await ReadStreamResponseAsync(
                            response,
                            model,
                            stopwatch,
                            attempt,
                            maxAttempts,
                            attemptStopwatch.ElapsedMilliseconds,
                            requestId,
                            inputTokenEstimate,
                            maxTokens,
                            attemptToken,
                            armIdleTimer).ConfigureAwait(false);

                        if (streamResponse.IsSuccess)
                        {
                            return streamResponse;
                        }

                        // If response was truncated due to token limit (finish_reason == length), do not retry
                        if (string.Equals(streamResponse.FinishReason, "length", StringComparison.OrdinalIgnoreCase) ||
                            streamResponse.FailureKind == AiFailureKind.TokenLimitExceeded)
                        {
                            return streamResponse;
                        }

                        // If streaming failed (e.g. interrupted or empty), retry if attempts remain
                        if (attempt < maxAttempts)
                        {
                            var delayMs = GetDelayMilliseconds(response, attempt, baseDelayMs, _maxRetryAfterMs);
                            await NotifyAsync(request, new AiAttemptEvent(attempt, maxAttempts, "StreamFailed", attemptStopwatch.ElapsedMilliseconds, true, delayMs, Detail: SanitizeDiagnosticText(streamResponse.ErrorMessage ?? string.Empty, 120), Model: model)).ConfigureAwait(false);
                            _logger.LogWarning(
                                "OpenAI-compatible API streaming response failed ({ErrorMessage}) on attempt {Attempt}/{MaxAttempts} (ElapsedMs: {ElapsedMs}, RequestId: {RequestId}, Model: {Model}). Retrying in {DelayMs}ms.",
                                streamResponse.ErrorMessage, attempt, maxAttempts, attemptStopwatch.ElapsedMilliseconds, requestId ?? "none", model, delayMs);

                            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        return streamResponse;
                    }
                    else
                    {
                        var responseContent = await response.Content
                            .ReadAsStringAsync(attemptToken)
                            .ConfigureAwait(false);

                        var completion = JsonSerializer.Deserialize<ChatCompletionResponse>(
                            responseContent,
                            JsonSerializerOptions.Web);

                        var content = completion?.Choices?.FirstOrDefault()?.Message?.Content;
                        var finishReason = completion?.Choices?.FirstOrDefault()?.FinishReason;

                        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning(
                                "OpenAI-compatible API non-streaming response reached output token limit (attempt {Attempt}/{MaxAttempts}, finish_reason: length, Model: {Model}).",
                                attempt, maxAttempts, model);

                            stopwatch.Stop();
                            return new AiResponse
                            {
                                Provider = ProviderName,
                                Model = completion?.Model ?? model,
                                Duration = stopwatch.Elapsed,
                                IsSuccess = false,
                                Content = content ?? string.Empty,
                                InputTokens = completion?.Usage?.PromptTokens ?? inputTokenEstimate,
                                OutputTokens = completion?.Usage?.CompletionTokens,
                                StatusCode = (int)response.StatusCode,
                                AttemptCount = attempt,
                                RequestId = requestId,
                                FailureKind = AiFailureKind.TokenLimitExceeded,
                                ErrorMessage = "AI response exhausted the configured output token limit before producing a complete result.",
                                FinishReason = "length",
                            };
                        }

                        if (string.IsNullOrWhiteSpace(content))
                        {
                            _logger.LogWarning(
                                "OpenAI-compatible API returned a response with no content on attempt {Attempt}/{MaxAttempts} (ElapsedMs: {ElapsedMs}, RequestId: {RequestId}, Model: {Model}).",
                                attempt, maxAttempts, attemptStopwatch.ElapsedMilliseconds, requestId ?? "none", model);

                            stopwatch.Stop();
                            return new AiResponse
                            {
                                Provider = ProviderName,
                                Model = model,
                                Duration = stopwatch.Elapsed,
                                IsSuccess = false,
                                StatusCode = (int)response.StatusCode,
                                AttemptCount = attempt,
                                RequestId = requestId,
                                FailureKind = AiFailureKind.TransientServiceUnavailable,
                                ErrorMessage = $"{_displayName} returned empty content after {attempt} attempt{(attempt == 1 ? "" : "s")}{(requestId != null ? $" (RequestId: {requestId})" : "")}.",
                                FinishReason = finishReason,
                            };
                        }

                        _logger.LogInformation(
                            "OpenAI-compatible API request succeeded on attempt {Attempt}/{MaxAttempts} (ElapsedMs: {ElapsedMs}, RequestId: {RequestId}, Model: {Model}, PromptTokens: {PromptTokens}, CompletionTokens: {CompletionTokens}, FinishReason: {FinishReason}, Streaming: false).",
                            attempt, maxAttempts, attemptStopwatch.ElapsedMilliseconds, requestId ?? "none", completion?.Model ?? model,
                            completion?.Usage?.PromptTokens ?? inputTokenEstimate, completion?.Usage?.CompletionTokens, finishReason ?? "unknown");

                        int? nonStreamReasoningTokens = null;
                        if (completion?.Usage != null)
                        {
                            // OpenAI-style usage extension details
                            nonStreamReasoningTokens = null;
                        }

                        stopwatch.Stop();
                        return new AiResponse
                        {
                            Provider = ProviderName,
                            Model = completion?.Model ?? model,
                            Content = content,
                            InputTokens = completion?.Usage?.PromptTokens ?? inputTokenEstimate,
                            OutputTokens = completion?.Usage?.CompletionTokens,
                            ReasoningTokens = nonStreamReasoningTokens,
                            Duration = stopwatch.Elapsed,
                            IsSuccess = true,
                            StatusCode = (int)response.StatusCode,
                            AttemptCount = attempt,
                            RequestId = requestId,
                            FailureKind = AiFailureKind.None,
                            FinishReason = finishReason,
                        };
                    }
                }

                // Handle Non-Success HTTP status
                var statusCode = (int)response.StatusCode;
                var errorBody = await response.Content
                    .ReadAsStringAsync(attemptToken)
                    .ConfigureAwait(false);

                var (errorCode, errorType, _) = ParseErrorBody(errorBody);
                var errorExcerpt = SanitizeDiagnosticText(errorBody, 300);

                bool isPermanentQuota = statusCode == 429 &&
                    (string.Equals(errorCode, "insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(errorCode, "account_deactivated", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(errorType, "insufficient_quota", StringComparison.OrdinalIgnoreCase));

                AiFailureKind failureKind;
                bool isTransient;

                if (isPermanentQuota)
                {
                    failureKind = AiFailureKind.Permanent;
                    isTransient = false;
                }
                else if (statusCode == 429)
                {
                    failureKind = AiFailureKind.RateLimited;
                    isTransient = true;
                }
                else if (statusCode is 502 or 503 or 504 or 500)
                {
                    failureKind = AiFailureKind.TransientServiceUnavailable;
                    isTransient = true;
                }
                else
                {
                    // Other 4xx (400, 401, 403, 404, etc.)
                    failureKind = AiFailureKind.Permanent;
                    isTransient = false;
                }

                if (isTransient && attempt < maxAttempts)
                {
                    var delayMs = GetDelayMilliseconds(response, attempt, baseDelayMs, _maxRetryAfterMs);
                    await NotifyAsync(request, new AiAttemptEvent(attempt, maxAttempts, "HttpStatus", attemptStopwatch.ElapsedMilliseconds, true, delayMs, statusCode, Model: model)).ConfigureAwait(false);
                    _logger.LogWarning(
                        "OpenAI-compatible API returned transient status code {StatusCode} on attempt {Attempt}/{MaxAttempts} (ElapsedMs: {ElapsedMs}, RequestId: {RequestId}, Model: {Model}, RetryAfter: {RetryAfter}, ErrorCode: {ErrorCode}, ErrorType: {ErrorType}, ErrorExcerpt: {ErrorExcerpt}, PayloadLength: {PayloadLength}, InputTokenEstimate: {InputTokens}, MaxOutputTokens: {MaxOutputTokens}, Streaming: {IsStreaming}). Retrying in {DelayMs}ms.",
                        statusCode, attempt, maxAttempts, attemptStopwatch.ElapsedMilliseconds, requestId ?? "none", model,
                        retryAfterHeader ?? "none", errorCode ?? "none", errorType ?? "none", errorExcerpt, payloadLength,
                        inputTokenEstimate, maxTokens?.ToString() ?? "default", _isStreaming, delayMs);

                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _logger.LogWarning(
                    "OpenAI-compatible API request failed with status code {StatusCode} on attempt {Attempt}/{MaxAttempts} (ElapsedMs: {ElapsedMs}, RequestId: {RequestId}, Model: {Model}, RetryAfter: {RetryAfter}, ErrorCode: {ErrorCode}, ErrorType: {ErrorType}, ErrorExcerpt: {ErrorExcerpt}, PayloadLength: {PayloadLength}, InputTokenEstimate: {InputTokens}, MaxOutputTokens: {MaxOutputTokens}, Streaming: {IsStreaming}).",
                    statusCode, attempt, maxAttempts, attemptStopwatch.ElapsedMilliseconds, requestId ?? "none", model,
                    retryAfterHeader ?? "none", errorCode ?? "none", errorType ?? "none", errorExcerpt, payloadLength,
                    inputTokenEstimate, maxTokens?.ToString() ?? "default", _isStreaming);

                stopwatch.Stop();

                string classifiedError = statusCode switch
                {
                    429 when isPermanentQuota => $"{_displayName} HTTP 429 quota exhausted after {attempt} attempt{(attempt == 1 ? "" : "s")}{(requestId != null ? $" (RequestId: {requestId})" : "")}.",
                    429 => $"{_displayName} HTTP 429 rate limited after {attempt} attempt{(attempt == 1 ? "" : "s")}{(requestId != null ? $" (RequestId: {requestId})" : "")}.",
                    502 => $"{_displayName} HTTP 502 bad gateway after {attempt} attempt{(attempt == 1 ? "" : "s")}{(requestId != null ? $" (RequestId: {requestId})" : "")}.",
                    503 => $"{_displayName} HTTP 503 service unavailable after {attempt} attempt{(attempt == 1 ? "" : "s")}{(requestId != null ? $" (RequestId: {requestId})" : "")}.",
                    504 => $"{_displayName} HTTP 504 gateway timeout after {attempt} attempt{(attempt == 1 ? "" : "s")}{(requestId != null ? $" (RequestId: {requestId})" : "")}.",
                    _ => $"{_displayName} HTTP {statusCode} after {attempt} attempt{(attempt == 1 ? "" : "s")}{(requestId != null ? $" (RequestId: {requestId})" : "")}."
                };

                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    StatusCode = statusCode,
                    AttemptCount = attempt,
                    RequestId = requestId,
                    FailureKind = failureKind,
                    ErrorMessage = classifiedError,
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller (for example the execution's cancel) stopped the call: report it and never retry.
                await NotifyAsync(request, new AiAttemptEvent(attempt, maxAttempts, "Cancelled", attemptStopwatch.ElapsedMilliseconds, false, Model: model)).ConfigureAwait(false);
                stopwatch.Stop();
                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    AttemptCount = attempt,
                    FailureKind = AiFailureKind.Cancelled,
                    ErrorMessage = $"{_displayName} request was cancelled.",
                };
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                attemptStopwatch.Stop();
                lastException = exception;
                var timeoutKind = totalCts.IsCancellationRequested ? "TotalTimeout" : idleCts.IsCancellationRequested ? "IdleTimeout" : "Timeout";
                var timeoutDelayMs = attempt < maxAttempts ? GetDelayMilliseconds(null, attempt, baseDelayMs, _maxRetryAfterMs) : (int?)null;
                await NotifyAsync(request, new AiAttemptEvent(attempt, maxAttempts, timeoutKind, attemptStopwatch.ElapsedMilliseconds, attempt < maxAttempts, timeoutDelayMs, Model: model)).ConfigureAwait(false);
                if (attempt < maxAttempts)
                {
                    var delayMs = timeoutDelayMs ?? 0;
                    _logger.LogWarning(
                        exception,
                        "OpenAI-compatible API request timed out on attempt {Attempt}/{MaxAttempts} (ElapsedMs: {ElapsedMs}, Model: {Model}, PayloadLength: {PayloadLength}, InputTokenEstimate: {InputTokens}, MaxOutputTokens: {MaxOutputTokens}, Streaming: {IsStreaming}). Retrying in {DelayMs}ms.",
                        attempt, maxAttempts, attemptStopwatch.ElapsedMilliseconds, model, payloadLength, inputTokenEstimate, maxTokens?.ToString() ?? "default", _isStreaming, delayMs);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _logger.LogWarning(
                    exception,
                    "OpenAI-compatible API request timed out on final attempt {Attempt}/{MaxAttempts} (ElapsedMs: {ElapsedMs}, Model: {Model}, PayloadLength: {PayloadLength}, InputTokenEstimate: {InputTokens}, MaxOutputTokens: {MaxOutputTokens}, Streaming: {IsStreaming}).",
                    attempt, maxAttempts, attemptStopwatch.ElapsedMilliseconds, model, payloadLength, inputTokenEstimate, maxTokens?.ToString() ?? "default", _isStreaming);

                stopwatch.Stop();
                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    AttemptCount = attempt,
                    FailureKind = AiFailureKind.TimeoutOrConnection,
                    ErrorMessage = timeoutKind switch
                    {
                        "TotalTimeout" => $"{_displayName} call exceeded the {(int)_totalTimeout.TotalSeconds}s total time limit ({attempt} attempts, {(int)stopwatch.Elapsed.TotalSeconds}s).",
                        "IdleTimeout" => $"{_displayName} stream sent no data for {(int)_streamIdleTimeout.TotalSeconds}s ({attempt} attempts, {(int)stopwatch.Elapsed.TotalSeconds}s).",
                        _ => $"{_displayName} request timed out after {(int)stopwatch.Elapsed.TotalSeconds}s ({attempt} attempts).",
                    },
                };
            }
            catch (HttpRequestException exception)
            {
                attemptStopwatch.Stop();
                lastException = exception;
                if (attempt < maxAttempts)
                {
                    var delayMs = GetDelayMilliseconds(null, attempt, baseDelayMs, _maxRetryAfterMs);
                    await NotifyAsync(request, new AiAttemptEvent(attempt, maxAttempts, "NetworkError", attemptStopwatch.ElapsedMilliseconds, true, delayMs, Detail: SanitizeDiagnosticText(exception.Message, 120), Model: model)).ConfigureAwait(false);
                    _logger.LogWarning(
                        exception,
                        "OpenAI-compatible API HTTP request exception on attempt {Attempt}/{MaxAttempts} (ElapsedMs: {ElapsedMs}, Model: {Model}, PayloadLength: {PayloadLength}, InputTokenEstimate: {InputTokens}, MaxOutputTokens: {MaxOutputTokens}, Streaming: {IsStreaming}). Retrying in {DelayMs}ms.",
                        attempt, maxAttempts, attemptStopwatch.ElapsedMilliseconds, model, payloadLength, inputTokenEstimate, maxTokens?.ToString() ?? "default", _isStreaming, delayMs);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _logger.LogWarning(
                    exception,
                    "An HTTP error occurred while calling the OpenAI-compatible API on final attempt {Attempt}/{MaxAttempts} (ElapsedMs: {ElapsedMs}, Model: {Model}, PayloadLength: {PayloadLength}, InputTokenEstimate: {InputTokens}, MaxOutputTokens: {MaxOutputTokens}, Streaming: {IsStreaming}).",
                    attempt, maxAttempts, attemptStopwatch.ElapsedMilliseconds, model, payloadLength, inputTokenEstimate, maxTokens?.ToString() ?? "default", _isStreaming);

                stopwatch.Stop();
                var sanitized = SanitizeDiagnosticText(exception.Message, 150);
                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    AttemptCount = attempt,
                    FailureKind = AiFailureKind.TimeoutOrConnection,
                    ErrorMessage = $"{_displayName} network error after {attempt} attempts: {sanitized}.",
                };
            }
            catch (JsonException exception)
            {
                attemptStopwatch.Stop();
                _logger.LogWarning(
                    exception,
                    "Failed to parse the OpenAI-compatible API response on attempt {Attempt}/{MaxAttempts} (ElapsedMs: {ElapsedMs}, Model: {Model}, PayloadLength: {PayloadLength}, InputTokenEstimate: {InputTokens}, MaxOutputTokens: {MaxOutputTokens}, Streaming: {IsStreaming}).",
                    attempt, maxAttempts, attemptStopwatch.ElapsedMilliseconds, model, payloadLength, inputTokenEstimate, maxTokens?.ToString() ?? "default", _isStreaming);

                stopwatch.Stop();
                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    AttemptCount = attempt,
                    FailureKind = AiFailureKind.Permanent,
                    ErrorMessage = $"{_displayName} response parsing failed after {attempt} attempt(s).",
                };
            }
            catch (Exception exception)
            {
                attemptStopwatch.Stop();
                _logger.LogWarning(
                    exception,
                    "An unexpected error occurred while calling the OpenAI-compatible API on attempt {Attempt}/{MaxAttempts} (ElapsedMs: {ElapsedMs}, Model: {Model}, PayloadLength: {PayloadLength}, InputTokenEstimate: {InputTokens}, MaxOutputTokens: {MaxOutputTokens}, Streaming: {IsStreaming}).",
                    attempt, maxAttempts, attemptStopwatch.ElapsedMilliseconds, model, payloadLength, inputTokenEstimate, maxTokens?.ToString() ?? "default", _isStreaming);

                stopwatch.Stop();
                var sanitized = SanitizeDiagnosticText(exception.Message, 150);
                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    AttemptCount = attempt,
                    FailureKind = AiFailureKind.Permanent,
                    ErrorMessage = $"{_displayName} unexpected failure after {attempt} attempt(s): {sanitized}.",
                };
            }
        }

        stopwatch.Stop();
        return new AiResponse
        {
            Provider = ProviderName,
            Model = model,
            Duration = stopwatch.Elapsed,
            IsSuccess = false,
            AttemptCount = attempt,
            FailureKind = AiFailureKind.Permanent,
            ErrorMessage = lastException?.Message ?? $"{_displayName} API request failed after {attempt} attempts.",
        };
    }

    /// <summary>Reports an attempt event to whoever asked (the execution feed). Observers can never break the call.</summary>
    private static async Task NotifyAsync(AiRequest request, AiAttemptEvent attemptEvent)
    {
        if (request.OnAttempt is null)
        {
            return;
        }

        try
        {
            await request.OnAttempt(attemptEvent).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Telemetry only.
        }
    }

    private async Task<AiResponse> ReadStreamResponseAsync(
        HttpResponseMessage response,
        string fallbackModel,
        Stopwatch totalStopwatch,
        int attempt,
        int maxAttempts,
        long headersElapsedMs,
        string? requestId,
        int inputTokenEstimate,
        int? maxOutputTokens,
        CancellationToken cancellationToken,
        Action? armIdleTimer = null)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        armIdleTimer?.Invoke();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var contentSb = new StringBuilder();
        var reasoningSb = new StringBuilder();
        const int maxAccumulatedBytes = 10 * 1024 * 1024; // 10 MB safety ceiling
        string? model = null;
        string? finishReason = null;
        int? promptTokens = null;
        int? completionTokens = null;
        int? reasoningTokens = null;
        bool receivedDone = false;
        var dataEvents = 0;
        string? streamError = null;
        var deltaKeys = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            while (true)
            {
                // Silence is measured from the moment we start waiting for the next line (keep-alive comments count as data).
                armIdleTimer?.Invoke();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith(':'))
                {
                    continue; // SSE comment / keepalive ping
                }

                if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var dataContent = trimmed.Substring(5).Trim();
                    if (string.Equals(dataContent, "[DONE]", StringComparison.OrdinalIgnoreCase))
                    {
                        receivedDone = true;
                        break;
                    }

                    dataEvents++;
                    try
                    {
                        using var doc = JsonDocument.Parse(dataContent);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                        {
                            model ??= modelEl.GetString();
                        }

                        if (root.TryGetProperty("choices", out var choicesEl) && choicesEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var choice in choicesEl.EnumerateArray())
                            {
                                if (choice.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.Object)
                                {
                                    foreach (var deltaProperty in deltaEl.EnumerateObject())
                                    {
                                        if (deltaKeys.Count < 12)
                                        {
                                            deltaKeys.Add(deltaProperty.Name);
                                        }
                                    }

                                    // Gateways in front of other vendors do not always send a plain string: parts arrays,
                                    // "text", or a whole "message" in the last chunk all carry the answer.
                                    var chunk = ExtractStreamText(deltaEl, "content") ?? ExtractStreamText(deltaEl, "text");
                                    if (!string.IsNullOrEmpty(chunk))
                                    {
                                        if (contentSb.Length + chunk.Length > maxAccumulatedBytes)
                                        {
                                            throw new InvalidOperationException("Streamed AI response exceeded maximum allowed buffer size.");
                                        }
                                        contentSb.Append(chunk);
                                    }

                                    if ((deltaEl.TryGetProperty("reasoning_content", out var reasoningEl) ||
                                         deltaEl.TryGetProperty("reasoning", out reasoningEl)) &&
                                        reasoningEl.ValueKind == JsonValueKind.String)
                                    {
                                        var reasoningChunk = reasoningEl.GetString();
                                        if (!string.IsNullOrEmpty(reasoningChunk) &&
                                            reasoningSb.Length + reasoningChunk.Length <= maxAccumulatedBytes)
                                        {
                                            reasoningSb.Append(reasoningChunk);
                                        }
                                    }
                                }

                                // A final chunk that carries the finished message instead of deltas.
                                if (contentSb.Length == 0 &&
                                    choice.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.Object)
                                {
                                    var whole = ExtractStreamText(messageEl, "content");
                                    if (!string.IsNullOrEmpty(whole))
                                    {
                                        contentSb.Append(whole);
                                    }
                                }

                                if (choice.TryGetProperty("finish_reason", out var finishReasonEl) && finishReasonEl.ValueKind == JsonValueKind.String)
                                {
                                    finishReason = finishReasonEl.GetString();
                                }
                            }
                        }

                        // Anthropic-style events relayed as-is.
                        if (root.TryGetProperty("type", out var eventTypeEl) && eventTypeEl.ValueKind == JsonValueKind.String &&
                            string.Equals(eventTypeEl.GetString(), "content_block_delta", StringComparison.Ordinal) &&
                            root.TryGetProperty("delta", out var blockDelta) && blockDelta.ValueKind == JsonValueKind.Object)
                        {
                            var text = ExtractStreamText(blockDelta, "text");
                            if (!string.IsNullOrEmpty(text) && contentSb.Length + text.Length <= maxAccumulatedBytes)
                            {
                                contentSb.Append(text);
                            }
                        }

                        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                        {
                            if (usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv))
                            {
                                promptTokens = ptv;
                            }
                            if (usageEl.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var ctv))
                            {
                                completionTokens = ctv;
                            }
                            if (usageEl.TryGetProperty("completion_tokens_details", out var ctd) && ctd.ValueKind == JsonValueKind.Object)
                            {
                                if (ctd.TryGetProperty("reasoning_tokens", out var rt) && rt.TryGetInt32(out var rtv))
                                {
                                    reasoningTokens = rtv;
                                }
                            }
                            else if (usageEl.TryGetProperty("reasoning_tokens", out var rtDirect) && rtDirect.TryGetInt32(out var rtvDirect))
                            {
                                reasoningTokens = rtvDirect;
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Non-JSON chunk line, ignore safely
                    }
                }
                else if (trimmed.StartsWith('{') && streamError is null)
                {
                    // An HTTP 200 whose body is a plain JSON error instead of SSE events.
                    var (errorCode, errorType, errorMessage) = ParseErrorBody(trimmed);
                    if (errorCode is not null || errorType is not null || errorMessage is not null)
                    {
                        streamError = SanitizeDiagnosticText($"{errorType} {errorCode} {errorMessage}", 160);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            totalStopwatch.Stop();
            _logger.LogWarning(
                ex,
                "OpenAI-compatible API SSE stream read interrupted on attempt {Attempt}/{MaxAttempts} (HeadersElapsedMs: {HeadersElapsedMs}, TotalElapsedMs: {TotalElapsedMs}, RequestId: {RequestId}, Model: {Model}).",
                attempt, maxAttempts, headersElapsedMs, totalStopwatch.ElapsedMilliseconds, requestId ?? "none", model ?? fallbackModel);

            var sanitizedEx = SanitizeDiagnosticText(ex.Message, 150);
            return new AiResponse
            {
                Provider = ProviderName,
                Model = model ?? fallbackModel,
                Duration = totalStopwatch.Elapsed,
                IsSuccess = false,
                StatusCode = (int)response.StatusCode,
                AttemptCount = attempt,
                RequestId = requestId,
                FailureKind = AiFailureKind.TransientServiceUnavailable,
                ErrorMessage = $"{_displayName} SSE stream interrupted after {attempt} attempt{(attempt == 1 ? "" : "s")}: {sanitizedEx}.",
                FinishReason = finishReason,
            };
        }

        totalStopwatch.Stop();
        var finalContent = contentSb.ToString();

        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "OpenAI-compatible API streaming response reached output token limit (attempt {Attempt}/{MaxAttempts}, finish_reason: length, reasoningChars: {ReasoningChars}, contentChars: {ContentChars}, Model: {Model}).",
                attempt, maxAttempts, reasoningSb.Length, finalContent.Length, model ?? fallbackModel);

            return new AiResponse
            {
                Provider = ProviderName,
                Model = model ?? fallbackModel,
                Duration = totalStopwatch.Elapsed,
                IsSuccess = false,
                Content = finalContent,
                InputTokens = promptTokens ?? inputTokenEstimate,
                OutputTokens = completionTokens,
                ReasoningTokens = reasoningTokens,
                StatusCode = (int)response.StatusCode,
                AttemptCount = attempt,
                RequestId = requestId,
                FailureKind = AiFailureKind.TokenLimitExceeded,
                ErrorMessage = "AI response exhausted the configured output token limit before producing a complete result.",
                FinishReason = "length",
            };
        }

        if (!receivedDone && finishReason == null)
        {
            _logger.LogWarning(
                "OpenAI-compatible API SSE stream ended before [DONE] on attempt {Attempt}/{MaxAttempts} (HeadersElapsedMs: {HeadersElapsedMs}, TotalElapsedMs: {TotalElapsedMs}, RequestId: {RequestId}, Model: {Model}).",
                attempt, maxAttempts, headersElapsedMs, totalStopwatch.ElapsedMilliseconds, requestId ?? "none", model ?? fallbackModel);

            return new AiResponse
            {
                Provider = ProviderName,
                Model = model ?? fallbackModel,
                Duration = totalStopwatch.Elapsed,
                IsSuccess = false,
                StatusCode = (int)response.StatusCode,
                AttemptCount = attempt,
                RequestId = requestId,
                FailureKind = AiFailureKind.TransientServiceUnavailable,
                ErrorMessage = $"{_displayName} SSE stream ended before [DONE] after {attempt} attempt{(attempt == 1 ? "" : "s")}{(requestId != null ? $" (RequestId: {requestId})" : "")}{(streamError is not null ? $" [{dataEvents} events, provider error: {streamError}]" : "")}.",
                FinishReason = finishReason,
            };
        }

        if (string.IsNullOrWhiteSpace(finalContent))
        {
            // Say what the stream really contained, so an empty answer from the provider can be told from one we failed to read.
            var streamFacts = $"{dataEvents} events, {reasoningSb.Length} reasoning chars, finish={finishReason ?? "none"}, completion_tokens={completionTokens?.ToString() ?? "n/a"}" +
                              (deltaKeys.Count > 0 ? $", delta fields: {string.Join('/', deltaKeys)}" : string.Empty) +
                              (streamError is not null ? $", provider error: {streamError}" : string.Empty);
            _logger.LogWarning(
                "OpenAI-compatible API streaming returned empty content on attempt {Attempt}/{MaxAttempts} (HeadersElapsedMs: {HeadersElapsedMs}, TotalElapsedMs: {TotalElapsedMs}, RequestId: {RequestId}, Model: {Model}, Stream: {StreamFacts}).",
                attempt, maxAttempts, headersElapsedMs, totalStopwatch.ElapsedMilliseconds, requestId ?? "none", model ?? fallbackModel, streamFacts);

            return new AiResponse
            {
                Provider = ProviderName,
                Model = model ?? fallbackModel,
                Duration = totalStopwatch.Elapsed,
                IsSuccess = false,
                StatusCode = (int)response.StatusCode,
                AttemptCount = attempt,
                RequestId = requestId,
                FailureKind = AiFailureKind.TransientServiceUnavailable,
                ErrorMessage = $"{_displayName} returned empty content after {attempt} attempt{(attempt == 1 ? "" : "s")}{(requestId != null ? $" (RequestId: {requestId})" : "")} [{streamFacts}].",
                FinishReason = finishReason,
            };
        }

        _logger.LogInformation(
            "OpenAI-compatible API streaming succeeded on attempt {Attempt}/{MaxAttempts} (HeadersElapsedMs: {HeadersElapsedMs}, TotalElapsedMs: {TotalElapsedMs}, RequestId: {RequestId}, Model: {Model}, PromptTokens: {PromptTokens}, CompletionTokens: {CompletionTokens}, ReasoningChars: {ReasoningChars}, FinishReason: {FinishReason}, Streaming: true).",
            attempt, maxAttempts, headersElapsedMs, totalStopwatch.ElapsedMilliseconds, requestId ?? "none", model ?? fallbackModel,
            promptTokens ?? inputTokenEstimate, completionTokens, reasoningSb.Length, finishReason ?? "stop");

        return new AiResponse
        {
            Provider = ProviderName,
            Model = model ?? fallbackModel,
            Content = finalContent,
            InputTokens = promptTokens ?? inputTokenEstimate,
            OutputTokens = completionTokens,
            ReasoningTokens = reasoningTokens,
            Duration = totalStopwatch.Elapsed,
            IsSuccess = true,
            StatusCode = (int)response.StatusCode,
            AttemptCount = attempt,
            RequestId = requestId,
            FailureKind = AiFailureKind.None,
            FinishReason = finishReason ?? "stop",
        };
    }

    /// <summary>Reads text from a property that is a string, an array of text parts, or a single part object.</summary>
    private static string? ExtractStreamText(JsonElement owner, string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Object:
                return element.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String
                    ? partText.GetString()
                    : null;
            case JsonValueKind.Array:
                var builder = new StringBuilder();
                foreach (var part in element.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(part.GetString());
                    }
                    else if (part.ValueKind == JsonValueKind.Object &&
                             part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
                             (!part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                              !string.Equals(type.GetString(), "thinking", StringComparison.OrdinalIgnoreCase)))
                    {
                        builder.Append(text.GetString());
                    }
                }

                return builder.Length > 0 ? builder.ToString() : null;
            default:
                return null;
        }
    }

    internal static int GetDelayMilliseconds(HttpResponseMessage? response, int attempt, int baseDelayMs, int maxRetryAfterMs = 30000)
    {
        if (response?.Headers.RetryAfter != null)
        {
            if (response.Headers.RetryAfter.Delta.HasValue)
            {
                var ms = (int)response.Headers.RetryAfter.Delta.Value.TotalMilliseconds;
                return Math.Clamp(ms, 0, maxRetryAfterMs);
            }
            if (response.Headers.RetryAfter.Date.HasValue)
            {
                var diff = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                var ms = (int)diff.TotalMilliseconds;
                return Math.Clamp(ms, 0, maxRetryAfterMs);
            }
        }

        var exponential = baseDelayMs * (int)Math.Pow(2, attempt - 1);
        var maxJitter = Math.Min(500, (int)(exponential * 0.25));
        var jitter = maxJitter > 0 ? Random.Shared.Next(0, maxJitter) : 0;
        return Math.Min(exponential + jitter, maxRetryAfterMs);
    }

    private static string? GetHeaderValue(HttpResponseHeaders? headers, string headerName)
    {
        if (headers != null && headers.TryGetValues(headerName, out var values))
        {
            return string.Join(",", values);
        }
        return null;
    }

    private static (string? Code, string? Type, string? Message) ParseErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                var code = errorEl.TryGetProperty("code", out var c) ? c.GetString() : null;
                var type = errorEl.TryGetProperty("type", out var t) ? t.GetString() : null;
                var msg = errorEl.TryGetProperty("message", out var m) ? m.GetString() : null;
                return (code, type, msg);
            }
        }
        catch
        {
            // Not a JSON error object
        }
        return (null, null, null);
    }

    private static string SanitizeDiagnosticText(string? text, int maxLength = 300)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sanitized = text;

        // Redact bearer tokens
        sanitized = Regex.Replace(sanitized, @"Bearer\s+[a-zA-Z0-9_\-\.]+", "Bearer [REDACTED]");
        // Redact github tokens
        sanitized = Regex.Replace(sanitized, @"gh[pousr]_[a-zA-Z0-9]+", "[REDACTED]");
        sanitized = Regex.Replace(sanitized, @"github_pat_[a-zA-Z0-9_]+", "[REDACTED]");
        // Redact api keys
        sanitized = Regex.Replace(sanitized, @"(?i)(?:api[-_]?key|key|secret)\s*[:=]\s*[""']?[a-zA-Z0-9_\-\.]+[""']?", "[REDACTED]");
        // Redact password in connection strings
        sanitized = Regex.Replace(sanitized, @"(?i)(Password|Pwd)\s*=\s*[^;]+", "$1=[REDACTED]");

        sanitized = sanitized.Replace("\r", " ").Replace("\n", " ");
        var trimmed = sanitized.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength) + "...";
    }

    private static int EstimateTokenCount(string? systemPrompt, string userPrompt)
    {
        var len = (systemPrompt?.Length ?? 0) + (userPrompt?.Length ?? 0);
        return Math.Max(1, len / 4);
    }

    private Uri BuildCompletionUri()
    {
        var baseUrl = _baseUrl.TrimEnd('/');

        // Many vendors publish their base URL with the version already included
        // (https://api.openai.com/v1, http://localhost:11434/v1); do not append it twice.
        var hasVersionSegment = Regex.IsMatch(baseUrl, @"/v\d+(beta)?$", RegexOptions.IgnoreCase);
        var path = hasVersionSegment ? "chat/completions" : "v1/chat/completions";
        return new Uri($"{baseUrl}/{path}", UriKind.Absolute);
    }

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<Message> Messages { get; set; } = new();

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("max_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxTokens { get; set; }

        [JsonPropertyName("max_completion_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxCompletionTokens { get; set; }

        [JsonPropertyName("reasoning_effort")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReasoningEffort { get; set; }

        [JsonPropertyName("stream_options")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public StreamOptions? StreamOptions { get; set; }
    }

    private sealed class StreamOptions
    {
        [JsonPropertyName("include_usage")]
        public bool IncludeUsage { get; set; }
    }

    private sealed class Message
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("reasoning_content")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReasoningContent { get; set; }
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("choices")]
        public IReadOnlyList<Choice>? Choices { get; set; }

        [JsonPropertyName("usage")]
        public Usage? Usage { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")]
        public Message? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class Usage
    {
        [JsonPropertyName("prompt_tokens")]
        public int? PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int? CompletionTokens { get; set; }
    }
}
