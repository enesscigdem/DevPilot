using DevPilot.Application.Executions.Models;

namespace DevPilot.Application.Executions.Ports;

/// <summary>Builds the app before and after an execution and screenshots it. Never throws for capture problems.</summary>
public interface IVisualCaptureService
{
    Task<VisualCaptureManifest> CaptureAsync(
        VisualCaptureRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads the stored visual check of an execution.</summary>
public interface IVisualArtifactReader
{
    Task<VisualCaptureManifest?> GetManifestAsync(
        string workspacePath,
        Guid executionId,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves a screenshot to a file path, or null when the name is not a known screenshot file.</summary>
    string? ResolveImagePath(string workspacePath, Guid executionId, string fileName);
}
