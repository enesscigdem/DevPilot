namespace DevPilot.Application.GitProviders;

/// <summary>Encrypts git host tokens before they are stored and decrypts them for git and API calls.</summary>
public interface IGitSecretProtector
{
    string Protect(string plainText);

    /// <summary>Returns null when the value cannot be decrypted (for example the key ring was lost).</summary>
    string? Unprotect(string protectedText);
}
