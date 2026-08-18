using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevPilot.Application.AiProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.AiProviders;

internal sealed class KimiAiProvider : IAiProvider
{
    public string ProviderName => AiProviderNames.Kimi;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<KimiAiProvider> _logger;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly bool _isConfigured;

    public KimiAiProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<KimiAiProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _baseUrl = configuration["AiProvider:Kimi:BaseUrl"] ?? string.Empty;
        _model = configuration["AiProvider:Kimi:Model"] ?? string.Empty;

        // API key is supplied via configuration (environment variable / secret).
        // Supported keys: AiProvider:Kimi:ApiKey (mapped from AiProvider__Kimi__ApiKey)
        // or KIMI_API_KEY.
        _apiKey = configuration["AiProvider:Kimi:ApiKey"]
            ?? configuration["KIMI_API_KEY"]
            ?? string.Empty;

        _isConfigured =
            !string.IsNullOrWhiteSpace(_apiKey) &&
            !string.IsNullOrWhiteSpace(_baseUrl) &&
            !string.IsNullOrWhiteSpace(_model);
    }

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var model = !string.IsNullOrWhiteSpace(request.Model) ? request.Model : _model;

        if (!_isConfigured)
        {
            _logger.LogWarning(
                "Kimi provider is not configured. Ensure AiProvider:Kimi:BaseUrl, AiProvider:Kimi:Model " +
                "and the API key (AiProvider:Kimi:ApiKey or KIMI_API_KEY environment variable) are set.");

            stopwatch.Stop();

            return new AiResponse
            {
                Provider = ProviderName,
                Model = model,
                Duration = stopwatch.Elapsed,
                IsSuccess = false,
                ErrorMessage = "Kimi provider is not configured. The base URL, model or API key is missing.",
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
        };

        var payloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        var payloadLength = payloadJson.Length;

        const int maxAttempts = 4;
        const int baseDelayMs = 200;

        int attempt = 0;
        HttpResponseMessage? lastResponse = null;
        string? lastResponseContent = null;
        Exception? lastException = null;

        while (attempt < maxAttempts)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var client = _httpClientFactory.CreateClient("Kimi");
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, BuildCompletionUri());
                requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                requestMessage.Content = new StringContent(payloadJson, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json"));

                lastResponse = await client
                    .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                lastResponseContent = await lastResponse.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (lastResponse.IsSuccessStatusCode)
                {
                    var completion = JsonSerializer.Deserialize<ChatCompletionResponse>(
                        lastResponseContent,
                        JsonSerializerOptions.Web);

                    var content = completion?.Choices?.FirstOrDefault()?.Message?.Content;

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        _logger.LogWarning("Kimi API returned a response with no content on attempt {Attempt}/{MaxAttempts}.", attempt, maxAttempts);

                        stopwatch.Stop();
                        return new AiResponse
                        {
                            Provider = ProviderName,
                            Model = model,
                            Duration = stopwatch.Elapsed,
                            IsSuccess = false,
                            ErrorMessage = "Kimi API returned a response with no content.",
                        };
                    }

                    stopwatch.Stop();
                    return new AiResponse
                    {
                        Provider = ProviderName,
                        Model = completion?.Model ?? model,
                        Content = content,
                        InputTokens = completion?.Usage?.PromptTokens,
                        OutputTokens = completion?.Usage?.CompletionTokens,
                        Duration = stopwatch.Elapsed,
                        IsSuccess = true,
                    };
                }

                var statusCode = (int)lastResponse.StatusCode;
                var isTransient = statusCode is 429 or 502 or 503 or 504;

                if (isTransient && attempt < maxAttempts)
                {
                    var delayMs = GetDelayMilliseconds(lastResponse, attempt, baseDelayMs);
                    _logger.LogWarning(
                        "Kimi API returned transient status code {StatusCode} on attempt {Attempt}/{MaxAttempts}. Retrying in {DelayMs}ms. PayloadLength: {PayloadLength}.",
                        statusCode, attempt, maxAttempts, delayMs, payloadLength);

                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var snippet = GetBoundedBodySnippet(lastResponseContent, 500);
                _logger.LogWarning(
                    "Kimi API request failed with non-success status code {StatusCode} on attempt {Attempt}/{MaxAttempts}. PayloadLength: {PayloadLength}. ResponseSnippet: {ResponseSnippet}",
                    statusCode, attempt, maxAttempts, payloadLength, snippet);

                stopwatch.Stop();
                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    ErrorMessage = $"Kimi API returned status code {statusCode}.",
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    ErrorMessage = "The request was cancelled.",
                };
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = exception;
                if (attempt < maxAttempts)
                {
                    var delayMs = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                    _logger.LogWarning(exception, "Kimi API request timed out on attempt {Attempt}/{MaxAttempts}. Retrying in {DelayMs}ms. PayloadLength: {PayloadLength}.", attempt, maxAttempts, delayMs, payloadLength);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _logger.LogWarning(exception, "Kimi API request timed out on final attempt {Attempt}/{MaxAttempts}. PayloadLength: {PayloadLength}.", attempt, maxAttempts, payloadLength);
                stopwatch.Stop();
                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    ErrorMessage = "The request to the Kimi API timed out.",
                };
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
                if (attempt < maxAttempts)
                {
                    var delayMs = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                    _logger.LogWarning(exception, "Kimi API HTTP request exception on attempt {Attempt}/{MaxAttempts}. Retrying in {DelayMs}ms. PayloadLength: {PayloadLength}.", attempt, maxAttempts, delayMs, payloadLength);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _logger.LogWarning(exception, "An HTTP error occurred while calling the Kimi API on final attempt {Attempt}/{MaxAttempts}. PayloadLength: {PayloadLength}.", attempt, maxAttempts, payloadLength);
                stopwatch.Stop();
                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    ErrorMessage = "An HTTP error occurred while calling the Kimi API.",
                };
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Failed to parse the Kimi API response on attempt {Attempt}/{MaxAttempts}. PayloadLength: {PayloadLength}.", attempt, maxAttempts, payloadLength);
                stopwatch.Stop();
                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    ErrorMessage = "Failed to parse the Kimi API response.",
                };
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "An unexpected error occurred while calling the Kimi API on attempt {Attempt}/{MaxAttempts}. PayloadLength: {PayloadLength}.", attempt, maxAttempts, payloadLength);
                stopwatch.Stop();
                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    ErrorMessage = "An unexpected error occurred while calling the Kimi API.",
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
            ErrorMessage = lastException?.Message ?? "Kimi API request failed after retries.",
        };
    }

    private static int GetDelayMilliseconds(HttpResponseMessage response, int attempt, int baseDelayMs)
    {
        if (response.Headers.RetryAfter != null)
        {
            if (response.Headers.RetryAfter.Delta.HasValue && response.Headers.RetryAfter.Delta.Value.TotalMilliseconds <= 10000)
            {
                return (int)response.Headers.RetryAfter.Delta.Value.TotalMilliseconds;
            }
            if (response.Headers.RetryAfter.Date.HasValue)
            {
                var diff = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                if (diff.TotalMilliseconds > 0 && diff.TotalMilliseconds <= 10000)
                {
                    return (int)diff.TotalMilliseconds;
                }
            }
        }

        return baseDelayMs * (int)Math.Pow(2, attempt - 1);
    }

    private static string GetBoundedBodySnippet(string? body, int maxLength)
    {
        if (string.IsNullOrEmpty(body)) return string.Empty;
        var trimmed = body.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
    }

    private Uri BuildCompletionUri()
    {
        var baseUrl = _baseUrl.TrimEnd('/');
        return new Uri($"{baseUrl}/v1/chat/completions", UriKind.Absolute);
    }

    private sealed class ChatCompletionRequest
    {
        public string Model { get; set; } = string.Empty;

        public List<Message> Messages { get; set; } = new();
    }

    private sealed class Message
    {
        public string Role { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;
    }

    private sealed class ChatCompletionResponse
    {
        public string? Model { get; set; }

        public IReadOnlyList<Choice>? Choices { get; set; }

        public Usage? Usage { get; set; }
    }

    private sealed class Choice
    {
        public Message? Message { get; set; }
    }

    private sealed class Usage
    {
        [JsonPropertyName("prompt_tokens")]
        public int? PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int? CompletionTokens { get; set; }
    }
}
