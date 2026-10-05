using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests;

public sealed class ScriptSyntaxGateTests
{
    private static string? FindWorkspaceWithTypeScript()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DevPilotWorkspaces", "executions");
        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).FirstOrDefault(d => File.Exists(Path.Combine(d, "node_modules", "typescript", "lib", "typescript.js")))
            : null;
    }

    [Fact]
    public async Task Unclosed_jsx_tag_is_reported_and_valid_code_passes()
    {
        var workspace = FindWorkspaceWithTypeScript();
        if (workspace == null)
        {
            return; // no TypeScript install available on this machine
        }

        var broken = "export const A = () => (\n  <Card>\n    <p>x</p>\n  </div>\n);\n";
        var ok = "export const A = () => (\n  <Card>\n    <p>x</p>\n  </Card>\n);\n";

        (await DeveloperAgent.FindScriptSyntaxErrorAsync(workspace, "src/A.tsx", broken, null, CancellationToken.None))
            .Should().Contain("syntax error");
        (await DeveloperAgent.FindScriptSyntaxErrorAsync(workspace, "src/A.tsx", ok, null, CancellationToken.None))
            .Should().BeNull();
        // A target that was already broken must never block the run.
        (await DeveloperAgent.FindScriptSyntaxErrorAsync(workspace, "src/A.tsx", broken, broken, CancellationToken.None))
            .Should().BeNull();
    }
}
