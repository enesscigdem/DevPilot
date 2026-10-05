using System.Text.Json;
using System.Text.RegularExpressions;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;

namespace DevPilot.Infrastructure.Executions.Visual;

/// <summary>
/// Stores the visual check next to the execution workspaces: {root}/visual/{executionId}. The root is derived from
/// the workspace path of the execution ({root}/executions/{executionId}), so no extra configuration is needed.
/// </summary>
public sealed class VisualArtifactStore : IVisualArtifactReader
{
    public const string ManifestFileName = "manifest.json";

    private static readonly Regex ImageNamePattern = new(
        @"^(?:before|after)-[a-z0-9-]{1,32}\.png$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string DirectoryFor(string workspacePath, Guid executionId)
    {
        var trimmed = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var executionsDirectory = Directory.GetParent(trimmed)
                                  ?? throw new InvalidOperationException("Workspace path has no parent directory.");
        var root = executionsDirectory.Parent ?? executionsDirectory;
        return Path.Combine(root.FullName, "visual", executionId.ToString());
    }

    public static async Task SaveManifestAsync(
        string directory,
        VisualCaptureManifest manifest,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VisualCaptureManifest?> GetManifestAsync(
        string workspacePath,
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }

        var path = Path.Combine(DirectoryFor(workspacePath, executionId), ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<VisualCaptureManifest>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public string? ResolveImagePath(string workspacePath, Guid executionId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !ImageNamePattern.IsMatch(fileName ?? string.Empty))
        {
            return null;
        }

        var path = Path.Combine(DirectoryFor(workspacePath, executionId), fileName!);
        return File.Exists(path) ? path : null;
    }
}
