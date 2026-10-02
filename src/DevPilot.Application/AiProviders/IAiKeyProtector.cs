namespace DevPilot.Application.AiProviders;

/// <summary>Encrypts model API keys before they are stored and decrypts them for outgoing calls.</summary>
public interface IAiKeyProtector
{
    string Protect(string plainText);

    /// <summary>Returns null when the value cannot be decrypted (for example the key ring was lost).</summary>
    string? Unprotect(string protectedText);
}
