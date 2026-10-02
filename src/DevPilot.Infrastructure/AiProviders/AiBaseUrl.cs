namespace DevPilot.Infrastructure.AiProviders;

/// <summary>Cleans up base URLs that users paste from vendor docs.</summary>
internal static class AiBaseUrl
{
    private const string ChatCompletionsSuffix = "/chat/completions";

    /// <summary>
    /// Trims whitespace and trailing slashes and drops a pasted "/chat/completions" endpoint path, so
    /// "https://host/v1/chat/completions" becomes "https://host/v1". The providers add the path themselves.
    /// </summary>
    public static string Normalize(string? baseUrl)
    {
        var url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (url.EndsWith(ChatCompletionsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^ChatCompletionsSuffix.Length].TrimEnd('/');
        }

        return url;
    }
}
