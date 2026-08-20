using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.GitProviders;

public sealed class GitHubOAuthStateService : IGitHubOAuthStateService
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private readonly byte[] _hmacKey;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _activeNonces = new();
    private readonly ILogger<GitHubOAuthStateService> _logger;

    public GitHubOAuthStateService(ILogger<GitHubOAuthStateService> logger)
    {
        _logger = logger;
        _hmacKey = new byte[32];
        RandomNumberGenerator.Fill(_hmacKey);
    }

    public string GenerateState(string? returnUrl = null)
    {
        CleanupExpiredNonces();

        var safeReturnUrl = SanitizeReturnUrl(returnUrl);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var expiresAt = DateTimeOffset.UtcNow.Add(StateLifetime);

        _activeNonces[nonce] = expiresAt;

        var payload = new StatePayload
        {
            Nonce = nonce,
            ExpiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds(),
            ReturnUrl = safeReturnUrl
        };

        var json = JsonSerializer.Serialize(payload);
        var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        using var hmac = new HMACSHA256(_hmacKey);
        var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadBase64));
        var signatureBase64 = Convert.ToBase64String(signatureBytes);

        return $"{payloadBase64}.{signatureBase64}";
    }

    public (bool IsValid, string SafeReturnUrl) ValidateAndConsumeState(string? state)
    {
        CleanupExpiredNonces();

        if (string.IsNullOrWhiteSpace(state))
        {
            _logger.LogWarning("OAuth state validation failed: state parameter is empty.");
            return (false, "/");
        }

        var parts = state.Split('.');
        if (parts.Length != 2)
        {
            _logger.LogWarning("OAuth state validation failed: invalid format.");
            return (false, "/");
        }

        var payloadBase64 = parts[0];
        var signatureBase64 = parts[1];

        // 1. Verify HMAC signature
        using var hmac = new HMACSHA256(_hmacKey);
        var expectedSig = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadBase64));
        byte[] providedSig;
        try
        {
            providedSig = Convert.FromBase64String(signatureBase64);
        }
        catch
        {
            _logger.LogWarning("OAuth state validation failed: malformed base64 signature.");
            return (false, "/");
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedSig, providedSig))
        {
            _logger.LogWarning("OAuth state validation failed: HMAC signature mismatch.");
            return (false, "/");
        }

        // 2. Deserialize payload
        StatePayload? payload;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payloadBase64));
            payload = JsonSerializer.Deserialize<StatePayload>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OAuth state validation failed: deserialization error.");
            return (false, "/");
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.Nonce))
        {
            _logger.LogWarning("OAuth state validation failed: empty payload.");
            return (false, "/");
        }

        // 3. Expiration check
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (payload.ExpiresAtUnixSeconds < now)
        {
            _logger.LogWarning("OAuth state validation failed: state has expired.");
            _activeNonces.TryRemove(payload.Nonce, out _);
            return (false, "/");
        }

        // 4. Single-use nonce check
        if (!_activeNonces.TryRemove(payload.Nonce, out var recordedExp) || recordedExp.ToUnixTimeSeconds() < now)
        {
            _logger.LogWarning("OAuth state validation failed: nonce is invalid, already consumed, or expired.");
            return (false, "/");
        }

        var safeReturnUrl = SanitizeReturnUrl(payload.ReturnUrl);
        return (true, safeReturnUrl);
    }

    public static string SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        var trimmed = returnUrl.Trim();

        // Must start with single slash, not double slash or slash-backslash (prevents protocol-relative external redirects)
        if (!trimmed.StartsWith('/') || trimmed.StartsWith("//") || trimmed.StartsWith("/\\"))
        {
            return "/";
        }

        // Must not contain scheme colon (prevents javascript:, http:, etc.) or whitespace or control characters
        if (trimmed.Contains(':') || trimmed.Contains('\r') || trimmed.Contains('\n') || trimmed.Contains('\t') || trimmed.Contains(' '))
        {
            return "/";
        }

        return trimmed;
    }

    private void CleanupExpiredNonces()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _activeNonces)
        {
            if (kvp.Value < now)
            {
                _activeNonces.TryRemove(kvp.Key, out _);
            }
        }
    }

    private sealed class StatePayload
    {
        public string Nonce { get; set; } = string.Empty;

        public long ExpiresAtUnixSeconds { get; set; }

        public string ReturnUrl { get; set; } = "/";
    }
}
