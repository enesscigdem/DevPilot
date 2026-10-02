using DevPilot.Application.AiProviders;
using Microsoft.AspNetCore.DataProtection;

namespace DevPilot.Infrastructure.AiProviders;

internal sealed class AiKeyProtector : IAiKeyProtector
{
    private const string Purpose = "DevPilot.AiModelApiKey.v1";

    private readonly IDataProtector _protector;

    public AiKeyProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plainText) => _protector.Protect(plainText);

    public string? Unprotect(string protectedText)
    {
        try
        {
            return _protector.Unprotect(protectedText);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The key ring changed or was lost; the user has to re-enter the key.
            return null;
        }
    }
}
