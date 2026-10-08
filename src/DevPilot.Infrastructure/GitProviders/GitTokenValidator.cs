using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DevPilot.Domain.Enums;

namespace DevPilot.Infrastructure.GitProviders;

public sealed record GitTokenValidationResult(bool IsValid, string? AccountLogin, string? ErrorMessage);

/// <summary>Checks a token against the host before it is stored, so a typo fails at connect time and not at the first clone.</summary>
public interface IGitTokenValidator
{
    Task<GitTokenValidationResult> ValidateAsync(
        GitProviderKind provider,
        string host,
        string token,
        CancellationToken cancellationToken = default);
}

public sealed class GitTokenValidator : IGitTokenValidator
{
    public const string HttpClientName = "GitHost";

    private readonly IHttpClientFactory _httpClientFactory;

    public GitTokenValidator(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<GitTokenValidationResult> ValidateAsync(
        GitProviderKind provider,
        string host,
        string token,
        CancellationToken cancellationToken = default)
    {
        if (provider != GitProviderKind.GitLab)
        {
            // Generic hosts expose no common user endpoint. The token is proven by the first clone.
            return new GitTokenValidationResult(true, null, null);
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/api/v4/user");
            request.Headers.Add("PRIVATE-TOKEN", token);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevPilot", "1.0"));

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new GitTokenValidationResult(false, null, "GitLab rejected this token. Check its value and that it has the api scope.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new GitTokenValidationResult(false, null, $"GitLab at '{host}' answered HTTP {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var login = doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() : null;
            return new GitTokenValidationResult(true, login, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new GitTokenValidationResult(false, null, $"Could not reach GitLab at '{host}': {ex.Message}");
        }
    }
}
