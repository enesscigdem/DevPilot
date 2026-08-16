namespace DevPilot.Application.Executions.Services;

public static class ExecutionSensitivePathClassifier
{
    private static readonly string[] SensitiveExactFileNames =
    {
        ".env",
        "id_rsa",
        "id_rsa.pub",
        "id_ed25519",
        "id_ecdsa",
        "secrets.json",
        "credentials.json"
    };

    private static readonly string[] SensitiveExtensions =
    {
        ".pem",
        ".key",
        ".pfx",
        ".p12",
        ".crt",
        ".cer",
        ".der",
        ".kdbx"
    };

    public static bool IsSensitivePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');

        // Check for .git directory or files
        if (normalized.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileName(normalized);

        // Exact file name match
        if (SensitiveExactFileNames.Any(s => fileName.Equals(s, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Starts with .env.
        if (fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Sensitive extensions
        if (SensitiveExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }
}
