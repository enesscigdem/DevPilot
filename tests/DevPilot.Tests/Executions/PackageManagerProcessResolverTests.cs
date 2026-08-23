using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class PackageManagerProcessResolverTests : IDisposable
{
    private readonly string _root;

    public PackageManagerProcessResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "devpilot-pm-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void UnixNpm_RemainsDirectLogicalInvocation()
    {
        var resolver = new PackageManagerProcessResolver(isWindows: false, new[] { Path.Combine(_root, "bin") });

        var resolved = resolver.TryResolve("npm", new[] { "run", "build" }, out var fileName, out var arguments, out var error);

        resolved.Should().BeTrue();
        error.Should().BeNull();
        fileName.Should().Be("npm");
        arguments.Should().Equal("run", "build");
        PackageManagerProcessResolver.IsForbiddenLauncher(fileName).Should().BeFalse();
    }

    [Fact]
    public void WindowsNpm_DoesNotExecuteExtensionlessShimOrCmdBatch()
    {
        var nodejs = CreateWindowsNodeJsLayout();
        var cursorHelper = Path.Combine(_root, "cursor", "resources", "app", "resources", "helpers");
        Directory.CreateDirectory(cursorHelper);
        File.WriteAllText(Path.Combine(cursorHelper, "node.exe"), string.Empty);
        File.WriteAllText(Path.Combine(nodejs, "npm"), "#!/bin/sh");
        File.WriteAllText(Path.Combine(nodejs, "npm.cmd"), "@ECHO OFF");

        var resolver = new PackageManagerProcessResolver(isWindows: true, new[] { cursorHelper, nodejs });

        var resolved = resolver.TryResolve("npm", new[] { "run", "build" }, out var fileName, out var arguments, out var error);

        resolved.Should().BeTrue(error);
        Path.GetFileName(fileName).Should().Be("node.exe");
        fileName.Should().NotContain(Path.Combine("cursor", "resources", "app", "resources", "helpers"));
        PackageManagerProcessResolver.IsInvalidWindowsNpmShim(fileName).Should().BeFalse();
        PackageManagerProcessResolver.IsForbiddenLauncher(fileName).Should().BeFalse();
        arguments.Should().HaveCount(3);
        Path.GetFileName(arguments[0]).Should().Be("npm-cli.js");
        arguments.Skip(1).Should().Equal("run", "build");
    }

    [Fact]
    public void WindowsNpm_WithoutCliJs_FailsExplicitlyInsteadOfUsingShell()
    {
        var bin = Path.Combine(_root, "only-shims");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "npm"), "#!/bin/sh");
        File.WriteAllText(Path.Combine(bin, "npm.cmd"), "@ECHO OFF");

        var resolver = new PackageManagerProcessResolver(isWindows: true, new[] { bin });

        var resolved = resolver.TryResolve("npm", new[] { "run", "build" }, out var fileName, out var arguments, out var error);

        resolved.Should().BeFalse();
        fileName.Should().BeEmpty();
        arguments.Should().BeEmpty();
        error.Should().Contain("node.exe + npm-cli.js");
        error.Should().Contain("extensionless");
    }

    [Fact]
    public void Resolver_NeverSelectsCmdOrPowerShell()
    {
        var source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "DevPilot.Infrastructure", "Executions", "BoundedProcessRunner.cs"));

        source.Should().Contain("UseShellExecute = false");
        source.Should().NotContain("FileName = \"cmd");
        source.Should().NotContain("FileName = \"powershell");

        var resolverSource = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "DevPilot.Infrastructure", "Executions", "PackageManagerProcessResolver.cs"));
        resolverSource.Should().NotContain("UseShellExecute = true");
        resolverSource.Should().NotContain("cmd.exe /c");
        resolverSource.Should().NotContain("FileName = \"cmd");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateWindowsNodeJsLayout()
    {
        var nodejs = Path.Combine(_root, "nodejs");
        Directory.CreateDirectory(Path.Combine(nodejs, "node_modules", "npm", "bin"));
        File.WriteAllText(Path.Combine(nodejs, "node.exe"), string.Empty);
        File.WriteAllText(Path.Combine(nodejs, "node_modules", "npm", "bin", "npm-cli.js"), "#!/usr/bin/env node");
        return nodejs;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DevPilot.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate DevPilot.sln.");
    }
}
