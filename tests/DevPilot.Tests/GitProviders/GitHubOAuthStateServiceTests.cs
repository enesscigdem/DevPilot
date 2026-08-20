using DevPilot.Infrastructure.GitProviders;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.GitProviders;

public sealed class GitHubOAuthStateServiceTests
{
    private readonly GitHubOAuthStateService _service = new(NullLogger<GitHubOAuthStateService>.Instance);

    [Fact]
    public void GenerateState_WithValidLocalReturnUrl_ProducesValidState()
    {
        // Act
        var state = _service.GenerateState("/review/a1b2c3d4-e5f6-7890");

        // Assert
        state.Should().NotBeNullOrWhiteSpace();
        var (isValid, safeReturnUrl) = _service.ValidateAndConsumeState(state);
        isValid.Should().BeTrue();
        safeReturnUrl.Should().Be("/review/a1b2c3d4-e5f6-7890");
    }

    [Theory]
    [InlineData("//evil.com", "/")]
    [InlineData("//evil.com/path", "/")]
    [InlineData("/\\evil.com", "/")]
    [InlineData("https://evil.com", "/")]
    [InlineData("http://evil.com/hack", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/review/123:attacker", "/")]
    [InlineData("relative/path", "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/review/123\r\nmalicious", "/")]
    [InlineData("/review/123 with space", "/")]
    public void ValidateAndConsumeState_MaliciousOrInvalidReturnPaths_SanitizedToRoot(string untrustedReturnUrl, string expectedSafeUrl)
    {
        // Act
        var state = _service.GenerateState(untrustedReturnUrl);
        var (isValid, safeReturnUrl) = _service.ValidateAndConsumeState(state);

        // Assert
        isValid.Should().BeTrue();
        safeReturnUrl.Should().Be(expectedSafeUrl);
    }

    [Fact]
    public void ValidateAndConsumeState_TamperedSignature_ReturnsInvalid()
    {
        // Arrange
        var state = _service.GenerateState("/review/123");
        var parts = state.Split('.');
        var tampered = parts[0] + ".corrupted_signature_xyz";

        // Act
        var (isValid, _) = _service.ValidateAndConsumeState(tampered);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void ValidateAndConsumeState_SingleUse_ReplayAttackRejected()
    {
        // Arrange
        var state = _service.GenerateState("/review/123");

        // First use: must succeed
        var (isValidFirst, _) = _service.ValidateAndConsumeState(state);
        isValidFirst.Should().BeTrue();

        // Second use (replay attack): must fail
        var (isValidSecond, _) = _service.ValidateAndConsumeState(state);
        isValidSecond.Should().BeFalse();
    }

    [Fact]
    public void ValidateAndConsumeState_EmptyOrNullState_ReturnsInvalid()
    {
        var (r1, _) = _service.ValidateAndConsumeState(null);
        r1.Should().BeFalse();

        var (r2, _) = _service.ValidateAndConsumeState("");
        r2.Should().BeFalse();

        var (r3, _) = _service.ValidateAndConsumeState("not_a_valid_state_format");
        r3.Should().BeFalse();
    }
}
