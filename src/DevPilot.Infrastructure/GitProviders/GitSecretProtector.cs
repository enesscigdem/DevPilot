using DevPilot.Application.GitProviders;
using Microsoft.AspNetCore.DataProtection;

namespace DevPilot.Infrastructure.GitProviders;

internal sealed class GitSecretProtector : IGitSecretProtector
{
    private const string Purpose = "DevPilot.GitConnectionToken.v1";

    private readonly IDataProtector _protector;

    public GitSecretProtector(IDataProtectionProvider provider)
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
            return null;
        }
    }
}
