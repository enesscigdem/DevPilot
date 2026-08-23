namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Resolves supported package-manager tools to a process-safe FileName + ArgumentList.
/// Discovery still stores the logical manager name (e.g. "npm"); Windows invocation
/// must not execute the extensionless npm shim or an unrestricted shell.
/// </summary>
public sealed class PackageManagerProcessResolver
{
    public static readonly IReadOnlySet<string> SupportedManagers = new HashSet<string>(StringComparer.Ordinal)
    {
        "npm", "pnpm", "yarn", "bun"
    };

    private static readonly string[] ForbiddenLaunchers =
    {
        "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
        "bash", "bash.exe", "sh", "sh.exe", "zsh", "zsh.exe"
    };

    private readonly bool _isWindows;
    private readonly IReadOnlyList<string> _pathDirectories;

    public PackageManagerProcessResolver()
        : this(OperatingSystem.IsWindows(), SplitPath(Environment.GetEnvironmentVariable("PATH")))
    {
    }

    public PackageManagerProcessResolver(bool isWindows, IReadOnlyList<string> pathDirectories)
    {
        _isWindows = isWindows;
        _pathDirectories = pathDirectories ?? Array.Empty<string>();
    }

    public bool TryResolve(
        string manager,
        IReadOnlyList<string> arguments,
        out string fileName,
        out IReadOnlyList<string> resolvedArguments,
        out string? errorMessage)
    {
        fileName = string.Empty;
        resolvedArguments = Array.Empty<string>();
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(manager) || !SupportedManagers.Contains(manager))
        {
            errorMessage = $"Package manager '{manager}' is not a supported deterministic launcher.";
            return false;
        }

        if (arguments.Any(argument => argument is null))
        {
            errorMessage = "Package manager arguments must be a structured argument list.";
            return false;
        }

        if (!_isWindows)
        {
            fileName = manager;
            resolvedArguments = arguments.ToList();
            return true;
        }

        return manager switch
        {
            "npm" => TryResolveNpm(arguments, out fileName, out resolvedArguments, out errorMessage),
            "bun" => TryResolveNativeBinary("bun.exe", arguments, "bun", out fileName, out resolvedArguments, out errorMessage),
            "pnpm" => TryResolveNodeCliOrNative(
                arguments,
                manager,
                new[] { Path.Combine("node_modules", "pnpm", "bin", "pnpm.cjs"), Path.Combine("node_modules", "pnpm", "bin", "pnpm.js") },
                "pnpm.exe",
                out fileName,
                out resolvedArguments,
                out errorMessage),
            "yarn" => TryResolveNodeCliOrNative(
                arguments,
                manager,
                new[] { Path.Combine("node_modules", "yarn", "bin", "yarn.js"), Path.Combine("node_modules", "corepack", "dist", "yarn.js") },
                "yarn.exe",
                out fileName,
                out resolvedArguments,
                out errorMessage),
            _ => Reject($"Package manager '{manager}' is not a supported deterministic launcher.", out fileName, out resolvedArguments, out errorMessage)
        };
    }

    public static bool IsForbiddenLauncher(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return true;
        }

        var name = Path.GetFileName(fileName);
        return ForbiddenLaunchers.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsInvalidWindowsNpmShim(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return true;
        }

        var name = Path.GetFileName(fileName);
        return name.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("npm.cmd", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("npm.bat", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryResolveNpm(
        IReadOnlyList<string> arguments,
        out string fileName,
        out IReadOnlyList<string> resolvedArguments,
        out string? errorMessage)
    {
        foreach (var directory in EnumerateCandidateDirectories())
        {
            var nodeExe = Path.Combine(directory, "node.exe");
            var npmCli = Path.Combine(directory, "node_modules", "npm", "bin", "npm-cli.js");
            if (IsUsableNodeHost(nodeExe) && File.Exists(npmCli))
            {
                return Accept(nodeExe, Prepend(npmCli, arguments), out fileName, out resolvedArguments, out errorMessage);
            }
        }

        return Reject(
            "npm CLI could not be resolved to a process-safe Windows launcher (node.exe + npm-cli.js). The extensionless npm shim and npm.cmd are not executed.",
            out fileName,
            out resolvedArguments,
            out errorMessage);
    }

    private bool TryResolveNativeBinary(
        string binaryName,
        IReadOnlyList<string> arguments,
        string manager,
        out string fileName,
        out IReadOnlyList<string> resolvedArguments,
        out string? errorMessage)
    {
        foreach (var directory in EnumerateCandidateDirectories())
        {
            var candidate = Path.Combine(directory, binaryName);
            if (File.Exists(candidate) && !IsForbiddenLauncher(candidate))
            {
                return Accept(candidate, arguments.ToList(), out fileName, out resolvedArguments, out errorMessage);
            }
        }

        return Reject(
            $"{manager} could not be resolved to a native Windows executable. Shell shims are not used.",
            out fileName,
            out resolvedArguments,
            out errorMessage);
    }

    private bool TryResolveNodeCliOrNative(
        IReadOnlyList<string> arguments,
        string manager,
        IReadOnlyList<string> relativeCliPaths,
        string nativeBinaryName,
        out string fileName,
        out IReadOnlyList<string> resolvedArguments,
        out string? errorMessage)
    {
        foreach (var directory in EnumerateCandidateDirectories())
        {
            var nodeExe = Path.Combine(directory, "node.exe");
            if (IsUsableNodeHost(nodeExe))
            {
                foreach (var relativeCli in relativeCliPaths)
                {
                    var cli = Path.Combine(directory, relativeCli);
                    if (File.Exists(cli))
                    {
                        return Accept(nodeExe, Prepend(cli, arguments), out fileName, out resolvedArguments, out errorMessage);
                    }
                }
            }

            var native = Path.Combine(directory, nativeBinaryName);
            if (File.Exists(native) && !IsForbiddenLauncher(native))
            {
                return Accept(native, arguments.ToList(), out fileName, out resolvedArguments, out errorMessage);
            }
        }

        return Reject(
            $"{manager} could not be resolved to a process-safe Windows launcher. Shell shims are not used.",
            out fileName,
            out resolvedArguments,
            out errorMessage);
    }

    private IEnumerable<string> EnumerateCandidateDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in _pathDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var full = Path.GetFullPath(directory);
            if (seen.Add(full))
            {
                yield return full;
            }
        }
    }

    private static bool IsUsableNodeHost(string nodeExe)
    {
        if (!File.Exists(nodeExe) || IsForbiddenLauncher(nodeExe))
        {
            return false;
        }

        var normalized = nodeExe.Replace('\\', '/');
        return !normalized.Contains("/cursor/resources/app/resources/helpers/", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> Prepend(string first, IReadOnlyList<string> arguments)
    {
        var resolved = new List<string>(arguments.Count + 1) { first };
        resolved.AddRange(arguments);
        return resolved;
    }

    private static bool Accept(
        string resolvedFileName,
        IReadOnlyList<string> resolvedArguments,
        out string fileName,
        out IReadOnlyList<string> arguments,
        out string? errorMessage)
    {
        if (IsForbiddenLauncher(resolvedFileName) || IsInvalidWindowsNpmShim(resolvedFileName))
        {
            return Reject("Resolved package-manager launcher is not process-safe.", out fileName, out arguments, out errorMessage);
        }

        fileName = resolvedFileName;
        arguments = resolvedArguments;
        errorMessage = null;
        return true;
    }

    private static bool Reject(
        string error,
        out string fileName,
        out IReadOnlyList<string> arguments,
        out string? errorMessage)
    {
        fileName = string.Empty;
        arguments = Array.Empty<string>();
        errorMessage = error;
        return false;
    }

    private static IReadOnlyList<string> SplitPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? Array.Empty<string>()
            : path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
