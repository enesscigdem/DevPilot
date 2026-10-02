using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DevPilot.Application.AiProviders;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.AiProviders;

/// <summary>
/// Google Gemini (generateContent API, streamed over SSE). Retries transient failures with
/// exponential backoff, mirroring the other providers.
/// </summary>
internal sealed class GeminiAiProvider : IAiProvider
{
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com";
    private const int MaxResponseChars = 10 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiEndpointSettings _settings;
    private readonly ILogger _logger;

    public GeminiAiProvider(IHttpClientFactory httpClientFactory, AiEndpointSettings settings, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
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

        var body = BuildBody(request, request.MaxTokens ?? _settings.MaxOutputTokens);
        var attempts = Math.Max(1, _settings.MaxAttempts);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var client = _httpClientFactory.CreateClient(OpenAiCompatibleProvider.HttpClientName);
                using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(model))
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
                {
                    message.Headers.Add("x-goog-api-key", _settings.ApiKey);
                }

                using var response = await client
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var result = await ReadStreamAsync(response, model, stopwatch, attempt, cancellationToken).ConfigureAwait(false);

                    // An interrupted or empty stream is transient; a finished answer (even a truncated one) is final.
                    if (result.IsSuccess || result.FailureKind is not AiFailureKind.TransientServiceUnavailable || attempt == attempts)
                    {
                        return result;
                    }
                }
                else
                {
                    var status = (int)response.StatusCode;
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var kind = ClassifyStatus(status);

                    if (IsTransient(kind) && attempt < attempts)
                    {
                        await Task.Delay(DelayFor(response, attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    stopwatch.Stop();
                    return new AiResponse
                    {
                        Provider = ProviderName,
                        Model = model,
                        Duration = stopwatch.Elapsed,
                        IsSuccess = false,
                        StatusCode = status,
                        AttemptCount = attempt,
                        FailureKind = kind,
                        ErrorMessage = $"{_settings.DisplayName} HTTP {status}: {ExtractError(errorBody)}",
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(model, stopwatch, AiFailureKind.Cancelled, $"{_settings.DisplayName} request was cancelled.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                if (attempt == attempts)
                {
                    return Failure(model, stopwatch, AiFailureKind.TimeoutOrConnection,
                        $"{_settings.DisplayName} network error after {attempt} attempts: {Sanitize(ex.Message)}");
                }
            }

            _logger.LogWarning("{Provider} attempt {Attempt}/{Attempts} failed transiently; retrying.", _settings.DisplayName, attempt, attempts);
            await Task.Delay(DelayFor(null, attempt), cancellationToken).ConfigureAwait(false);
        }

        return Failure(model, stopwatch, AiFailureKind.Permanent, $"{_settings.DisplayName} request failed.");
    }

    private async Task<AiResponse> ReadStreamAsync(
        HttpResponseMessage response,
        string model,
        Stopwatch stopwatch,
        int attempt,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var content = new StringBuilder();
        string? finishReason = null;
        string? blockReason = null;
        string? responseModel = null;
        int? promptTokens = null, candidateTokens = null, thoughtTokens = null;

        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                JsonNode? chunk;
                try
                {
                    chunk = JsonNode.Parse(line[5..].Trim());
                }
                catch (JsonException)
                {
                    continue;
                }

                responseModel ??= chunk?["modelVersion"]?.GetValue<string>();
                blockReason ??= chunk?["promptFeedback"]?["blockReason"]?.GetValue<string>();

                var candidate = chunk?["candidates"]?[0];
                if (candidate?["content"]?["parts"] is JsonArray parts)
                {
                    foreach (var part in parts)
                    {
                        // "thought" parts are the model's reasoning summary, not the answer.
                        if (part?["thought"]?.GetValue<bool>() == true)
                        {
                            continue;
                        }

                        var text = part?["text"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(text) && content.Length + text.Length <= MaxResponseChars)
                        {
                            content.Append(text);
                        }
                    }
                }

                finishReason = candidate?["finishReason"]?.GetValue<string>() ?? finishReason;

                if (chunk?["usageMetadata"] is JsonNode usage)
                {
                    promptTokens = usage["promptTokenCount"]?.GetValue<int>() ?? promptTokens;
                    candidateTokens = usage["candidatesTokenCount"]?.GetValue<int>() ?? candidateTokens;
                    thoughtTokens = usage["thoughtsTokenCount"]?.GetValue<int>() ?? thoughtTokens;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return Failure(model, stopwatch, AiFailureKind.TransientServiceUnavailable,
                $"{_settings.DisplayName} stream interrupted: {Sanitize(ex.Message)}");
        }

        stopwatch.Stop();
        var result = new AiResponse
        {
            Provider = ProviderName,
            Model = string.IsNullOrWhiteSpace(responseModel) ? model : responseModel,
            Content = content.ToString(),
            InputTokens = promptTokens,
            // Thinking tokens are billed as output, so they belong in the output total.
            OutputTokens = candidateTokens is null && thoughtTokens is null ? null : (candidateTokens ?? 0) + (thoughtTokens ?? 0),
            ReasoningTokens = thoughtTokens,
            Duration = stopwatch.Elapsed,
            StatusCode = (int)response.StatusCode,
            AttemptCount = attempt,
            FinishReason = finishReason,
        };

        if (blockReason is not null)
        {
            result.FailureKind = AiFailureKind.Permanent;
            result.ErrorMessage = $"{_settings.DisplayName} blocked the prompt ({blockReason}).";
        }
        else if (string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
        {
            result.FailureKind = AiFailureKind.TokenLimitExceeded;
            result.FinishReason = "length";
            result.ErrorMessage = "AI response exhausted the configured output token limit before producing a complete result.";
        }
        else if (finishReason is not null and not "STOP")
        {
            // SAFETY, RECITATION, PROHIBITED_CONTENT, ...: retrying the same prompt will not help.
            result.FailureKind = AiFailureKind.Permanent;
            result.ErrorMessage = $"{_settings.DisplayName} stopped the answer ({finishReason}).";
        }
        else if (content.Length == 0)
        {
            result.FailureKind = AiFailureKind.TransientServiceUnavailable;
            result.ErrorMessage = $"{_settings.DisplayName} returned empty content.";
        }
        else
        {
            result.IsSuccess = true;
            result.FailureKind = AiFailureKind.None;
        }

        return result;
    }

    private static string BuildBody(AiRequest request, int? maxTokens)
    {
        var root = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = request.UserPrompt }),
            }),
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            root["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemPrompt }),
            };
        }

        if (maxTokens is > 0)
        {
            root["generationConfig"] = new JsonObject { ["maxOutputTokens"] = maxTokens };
        }

        return root.ToJsonString();
    }

    private Uri BuildUri(string model)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_settings.BaseUrl) ? DefaultBaseUrl : _settings.BaseUrl.TrimEnd('/');
        var hasVersion = Regex.IsMatch(baseUrl, @"/v\d+(beta)?$", RegexOptions.IgnoreCase);
        var prefix = hasVersion ? baseUrl : $"{baseUrl}/v1beta";
        return new Uri($"{prefix}/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse", UriKind.Absolute);
    }

    private static AiFailureKind ClassifyStatus(int status) =>
        status switch
        {
            429 => AiFailureKind.RateLimited,
            500 or 502 or 503 or 504 => AiFailureKind.TransientServiceUnavailable,
            _ => AiFailureKind.Permanent,
        };

    private static bool IsTransient(AiFailureKind kind) =>
        kind is AiFailureKind.RateLimited or AiFailureKind.TransientServiceUnavailable;

    private static int DelayFor(HttpResponseMessage? response, int attempt)
    {
        if (response?.Headers.RetryAfter?.Delta is { } delta)
        {
            return Math.Clamp((int)delta.TotalMilliseconds, 0, 30000);
        }

        var exponential = 1000 * (int)Math.Pow(2, attempt - 1);
        return Math.Min(exponential + Random.Shared.Next(0, 500), 30000);
    }

    private static string ExtractError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "no details";
        }

        if (body.TrimStart().StartsWith('<'))
        {
            return "the server answered with a web page, not an API response. Check the base URL and the API type.";
        }

        try
        {
            var message = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
            return Sanitize(message ?? body);
        }
        catch (JsonException)
        {
            return Sanitize(body);
        }
    }

    // Keep error text single-line and short; never let a key echoed by a proxy reach logs or the UI.
    private static string Sanitize(string text)
    {
        var cleaned = Regex.Replace(text, @"(?i)(api[-_]?key|key)\s*[:=]\s*[""']?[\w\-\.]+[""']?", "[REDACTED]")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
        return cleaned.Length <= 300 ? cleaned : cleaned[..300] + "...";
    }

    private AiResponse Failure(string model, Stopwatch stopwatch, AiFailureKind kind, string message)
    {
        stopwatch.Stop();
        return new AiResponse
        {
            Provider = ProviderName,
            Model = model,
            Duration = stopwatch.Elapsed,
            IsSuccess = false,
            FailureKind = kind,
            ErrorMessage = message,
        };
    }
}
