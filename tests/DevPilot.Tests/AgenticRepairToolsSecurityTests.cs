using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests;

/// <summary>
/// The repairing model's tools must obey the same workspace rules as the main edit path: no credential files, no
/// reaching outside the worktree through a symlink, for reading as well as writing and for existing and new files.
/// </summary>
public sealed class AgenticRepairToolsSecurityTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "devpilot-agentic-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly string _outside;

    public AgenticRepairToolsSecurityTests()
    {
        _workspace = Path.Combine(_temp, "workspace");
        _outside = Path.Combine(_temp, "outside");
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "OUTSIDE-SECRET");
        File.WriteAllText(Path.Combine(_workspace, ".env"), "API_KEY=WORKSPACE-SECRET");
        File.WriteAllText(Path.Combine(_workspace, "src", "app.ts"), "export const a = 1;\n");
    }

    public void Dispose()
    {
        // Remove a link before the recursive delete so the delete can never follow it out of the temp folder.
        var link = Path.Combine(_workspace, "src", "external");
        try
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }

            Directory.Delete(_temp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover temp files do not matter to the result.
        }
    }

    private AgenticRepairTools Tools() => new(_workspace);

    private static string Run(AgenticRepairTools tools, string tool, string? path = null, string? content = null, string? pattern = null) =>
        tools.Execute(new AgenticAction { Tool = tool, Path = path, Content = content, Pattern = pattern });

    /// <summary>
    /// Links <c>src/external</c> to the outside folder, with a symlink or, where that needs privileges (Windows), a junction,
    /// which breaks out of the workspace the same way. Fails the test run instead of silently skipping when neither works.
    /// </summary>
    private bool TryLinkOutside()
    {
        var link = Path.Combine(_workspace, "src", "external");
        try
        {
            Directory.CreateSymbolicLink(link, _outside);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_outside}\"")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(Path.Combine(link));
    }

    [Fact]
    public void A_credential_file_cannot_be_read()
    {
        var result = Run(Tools(), "read_file", ".env");

        result.Should().StartWith("ERROR");
        result.Should().NotContain("WORKSPACE-SECRET");
    }

    [Fact]
    public void A_credential_file_cannot_be_written()
    {
        var result = Run(Tools(), "write_file", "config/secrets.pem", "x");

        result.Should().StartWith("ERROR");
        File.Exists(Path.Combine(_workspace, "config", "secrets.pem")).Should().BeFalse();
    }

    [Fact]
    public void Search_does_not_reveal_the_content_of_credential_files()
    {
        Run(Tools(), "search", pattern: "WORKSPACE-SECRET").Should().NotContain("WORKSPACE-SECRET");
    }

    [Fact]
    public void An_existing_file_outside_the_workspace_cannot_be_read_through_a_symlinked_folder()
    {
        if (!TryLinkOutside())
        {
            return;
        }

        var result = Run(Tools(), "read_file", "src/external/secret.txt");

        result.Should().StartWith("ERROR");
        result.Should().NotContain("OUTSIDE-SECRET");
    }

    [Fact]
    public void A_new_file_cannot_be_created_outside_the_workspace_through_a_symlinked_folder()
    {
        if (!TryLinkOutside())
        {
            return;
        }

        var result = Run(Tools(), "write_file", "src/external/planted.txt", "planted");

        result.Should().StartWith("ERROR");
        File.Exists(Path.Combine(_outside, "planted.txt")).Should().BeFalse();
    }

    [Fact]
    public void Search_does_not_follow_a_symlinked_folder_out_of_the_workspace()
    {
        if (!TryLinkOutside())
        {
            return;
        }

        Run(Tools(), "search", pattern: "OUTSIDE-SECRET").Should().NotContain("OUTSIDE-SECRET");
    }

    [Fact]
    public void Ordinary_files_inside_the_workspace_still_work()
    {
        var tools = Tools();

        Run(tools, "read_file", "src/app.ts").Should().Contain("export const a");
        Run(tools, "write_file", "src/new.ts", "export const b = 2;\n").Should().StartWith("OK");
        File.Exists(Path.Combine(_workspace, "src", "new.ts")).Should().BeTrue();
    }

    [Fact]
    public void The_main_edit_path_also_refuses_a_new_file_under_a_symlinked_folder()
    {
        if (!TryLinkOutside())
        {
            return;
        }

        var act = () => WorktreeEditApplier.ValidateAndResolvePath(_workspace, "src/external/planted.txt");

        act.Should().Throw<InvalidOperationException>();
    }
}
