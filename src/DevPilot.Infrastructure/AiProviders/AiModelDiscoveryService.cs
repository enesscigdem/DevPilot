using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DevPilot.Application.AiProviders;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.AiProviders;

internal sealed class AiModelDiscoveryService : IAiModelDiscoveryService
{
    private const string AnthropicDefaultBaseUrl = "https://api.anthropic.com";
    private const string GeminiDefaultBaseUrl = "https://generativelanguage.googleapis.com";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly DevPilotDbContext _db;
    private readonly IAiKeyProtector _keyProtector;
    private readonly IHttpClientFactory _httpClientFactory;

    public AiModelDiscoveryService(DevPilotDbContext db, IAiKeyProtector keyProtector, IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _keyProtector = keyProtector;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<DiscoverAiModelsResultDto> DiscoverAsync(DiscoverAiModelsRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = request.BaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Base URL must be an absolute http or https address.");
        }

        var apiKey = await ResolveKeyAsync(request, uri, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey) && !uri.IsLoopback)
        {
            return Fail("Enter the API key first, then load the models.");
        }

        using var message = BuildRequest(request.AdapterType, baseUrl, apiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var client = _httpClientFactory.CreateClient(OpenAiCompatibleProvider.HttpClientName);
            using var response = await client.SendAsync(message, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Fail($"The provider answered HTTP {(int)response.StatusCode}: {ExtractError(body)}");
            }

            var models = Parse(request.AdapterType, body);
            return models.Count == 0
                ? Fail("The provider returned no models for this key.")
                : new DiscoverAiModelsResultDto { Success = true, Message = $"{models.Count} models found.", Models = models };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("The provider did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return Fail($"Could not reach the provider: {Sanitize(ex.Message)}");
        }
        catch (System.Text.Json.JsonException)
        {
            return Fail("The provider answered with something that is not a model list. Check the base URL.");
        }
    }

    private async Task<string?> ResolveKeyAsync(DiscoverAiModelsRequest request, Uri requestedUri, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return request.ApiKey.Trim();
        }

        if (request.ExistingModelId is not { } id)
        {
            return null;
        }

        var stored = await _db.AiModelConfigs.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (stored?.ProtectedApiKey is null)
        {
            return null;
        }

        // The stored key may only ever be sent to the host it was saved for.
        if (!Uri.TryCreate(stored.BaseUrl, UriKind.Absolute, out var storedUri)
            || !string.Equals(storedUri.Authority, requestedUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The endpoint changed. Enter the API key again so it is not sent to a different host.");
        }

        return _keyProtector.Unprotect(stored.ProtectedApiKey);
    }

    private static HttpRequestMessage BuildRequest(AiAdapterType adapter, string baseUrl, string? apiKey)
    {
        HttpRequestMessage message;
        switch (adapter)
        {
            case AiAdapterType.Claude:
                message = new HttpRequestMessage(HttpMethod.Get, $"{OrDefault(baseUrl, AnthropicDefaultBaseUrl)}/v1/models?limit=100");
                message.Headers.Add("anthropic-version", "2023-06-01");
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    message.Headers.Add("x-api-key", apiKey);
                }

                break;

            case AiAdapterType.Gemini:
                var geminiBase = OrDefault(baseUrl, GeminiDefaultBaseUrl);
                var prefix = HasVersionSegment(geminiBase) ? geminiBase : $"{geminiBase}/v1beta";
                message = new HttpRequestMessage(HttpMethod.Get, $"{prefix}/models?pageSize=1000");
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    message.Headers.Add("x-goog-api-key", apiKey);
                }

                break;

            default:
                var path = HasVersionSegment(baseUrl) ? "models" : "v1/models";
                message = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{path}");
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }

                break;
        }

        return message;
    }

    private static List<AiModelOptionDto> Parse(AiAdapterType adapter, string body)
    {
        var root = JsonNode.Parse(body);
        var result = new List<AiModelOptionDto>();

        if (adapter == AiAdapterType.Gemini)
        {
            foreach (var item in root?["models"]?.AsArray() ?? new JsonArray())
            {
                var methods = item?["supportedGenerationMethods"]?.AsArray();
                var canGenerate = methods is null
                    || methods.Any(m => string.Equals(m?.GetValue<string>(), "generateContent", StringComparison.Ordinal));
                var name = item?["name"]?.GetValue<string>();
                if (canGenerate && !string.IsNullOrWhiteSpace(name))
                {
                    result.Add(new AiModelOptionDto
                    {
                        Id = name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name,
                        DisplayName = item?["displayName"]?.GetValue<string>(),
                    });
                }
            }
        }
        else
        {
            foreach (var item in root?["data"]?.AsArray() ?? new JsonArray())
            {
                var id = item?["id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    // Anthropic calls it display_name, OpenRouter just "name".
                    result.Add(new AiModelOptionDto
                    {
                        Id = id,
                        DisplayName = item?["display_name"]?.GetValue<string>() ?? item?["name"]?.GetValue<string>(),
                    });
                }
            }
        }

        return result.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string OrDefault(string baseUrl, string fallback) => string.IsNullOrWhiteSpace(baseUrl) ? fallback : baseUrl;

    private static bool HasVersionSegment(string baseUrl) =>
        Regex.IsMatch(baseUrl, @"/v\d+(beta)?$", RegexOptions.IgnoreCase);

    private static DiscoverAiModelsResultDto Fail(string message) => new() { Success = false, Message = message };

    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "no details";
        }

        try
        {
            var error = JsonNode.Parse(body)?["error"];
            var text = error is JsonObject ? error["message"]?.GetValue<string>() : error?.GetValue<string>();
            return Sanitize(text ?? body);
        }
        catch (System.Text.Json.JsonException)
        {
            return Sanitize(body);
        }
    }

    private static string Sanitize(string text)
    {
        var cleaned = Regex.Replace(text, @"(?i)(api[-_]?key|key)\s*[:=]\s*[""']?[\w\-\.]+[""']?", "[REDACTED]")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
        return cleaned.Length <= 300 ? cleaned : cleaned[..300] + "...";
    }
}
