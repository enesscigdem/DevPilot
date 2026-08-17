using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.RepositoryWorkspaces.Dtos;

namespace DevPilot.Application.RepositoryWorkspaces.Ports;

public interface IRepositoryStructureScanner
{
    Task<List<WorkspaceFileNodeDto>> ScanStructureAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);

    Task<List<WorkspaceTechnologyDto>> DetectTechnologiesAsync(
        string repositoryPath,
        RepositoryAnalysisResult? roslynResult,
        CancellationToken cancellationToken = default);

    Task<int> CountProjectFilesAsync(
        string projectFilePath,
        string repositoryPath,
        CancellationToken cancellationToken = default);
}
