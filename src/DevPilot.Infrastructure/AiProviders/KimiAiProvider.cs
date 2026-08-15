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

        try
        {
            using var client = _httpClientFactory.CreateClient("Kimi");

            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, BuildCompletionUri());
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            requestMessage.Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                "application/json");

            using var response = await client
                .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var responseContent = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Kimi API returned non-success status code {StatusCode}.",
                    response.StatusCode);

                stopwatch.Stop();

                return new AiResponse
                {
                    Provider = ProviderName,
                    Model = model,
                    Duration = stopwatch.Elapsed,
                    IsSuccess = false,
                    ErrorMessage = $"Kimi API returned status code {(int)response.StatusCode}.",
                };
            }

            var completion = JsonSerializer.Deserialize<ChatCompletionResponse>(
                responseContent,
                JsonSerializerOptions.Web);

            var content = completion?.Choices?.FirstOrDefault()?.Message?.Content;

            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("Kimi API returned a response with no content.");

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
            _logger.LogWarning(exception, "The request to the Kimi API timed out.");

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
            _logger.LogWarning(exception, "An HTTP error occurred while calling the Kimi API.");

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
            _logger.LogWarning(exception, "Failed to parse the Kimi API response.");

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
            _logger.LogWarning(exception, "An unexpected error occurred while calling the Kimi API.");

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
