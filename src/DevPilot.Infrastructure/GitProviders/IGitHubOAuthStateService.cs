namespace DevPilot.Infrastructure.GitProviders;

public interface IGitHubOAuthStateService
{
    string GenerateState(string? returnUrl = null);

    (bool IsValid, string SafeReturnUrl) ValidateAndConsumeState(string? state);
}
