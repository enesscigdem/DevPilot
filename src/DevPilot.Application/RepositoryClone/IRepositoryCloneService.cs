namespace DevPilot.Application.RepositoryClone;

public interface IRepositoryCloneService
{
    Task<CloneResult> CloneAsync(CloneRequest request, CancellationToken cancellationToken = default);
}
