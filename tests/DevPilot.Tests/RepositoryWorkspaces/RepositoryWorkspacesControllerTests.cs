using DevPilot.Api.Controllers;
using DevPilot.Application.RepositoryWorkspaces.Commands.CreateRepositoryWorkspace;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DevPilot.Tests.RepositoryWorkspaces;

public class RepositoryWorkspacesControllerTests : IDisposable
{
    private readonly DevPilotDbContext _dbContext;
    private readonly FakeCreateWorkspaceCommandHandler _commandHandler = new();
    private readonly RepositoryWorkspacesController _controller;

    public RepositoryWorkspacesControllerTests()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase(databaseName: "ControllerTestDb_" + Guid.NewGuid().ToString("N"))
            .Options;

        _dbContext = new DevPilotDbContext(options);
        _controller = new RepositoryWorkspacesController(_dbContext, _commandHandler);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [Fact]
    public async Task Create_ValidRequest_ReturnsCreatedAtActionWithWorkspaceDto()
    {
        var workspaceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _commandHandler.ResultToReturn = new CreateRepositoryWorkspaceResult
        {
            Success = true,
            Workspace = new RepositoryWorkspaceDto
            {
                Id = workspaceId,
                Owner = "enesscigdem",
                Repository = "DevPilot",
                Branch = "master",
                Status = RepositoryWorkspaceStatus.Completed,
                CommitSha = "abc1234",
                CreatedAt = now,
                UpdatedAt = now,
            },
        };

        var dto = new CreateRepositoryWorkspaceDto
        {
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "master",
        };

        var response = await _controller.Create(dto, CancellationToken.None);

        var createdAtResult = response.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdAtResult.StatusCode.Should().Be(StatusCodes.Status201Created);
        createdAtResult.ActionName.Should().Be(nameof(RepositoryWorkspacesController.GetById));

        var returnedDto = createdAtResult.Value.Should().BeOfType<RepositoryWorkspaceDto>().Subject;
        returnedDto.Id.Should().Be(workspaceId);
        returnedDto.Owner.Should().Be("enesscigdem");
        returnedDto.Repository.Should().Be("DevPilot");
        returnedDto.Branch.Should().Be("master");
        returnedDto.Status.Should().Be(RepositoryWorkspaceStatus.Completed);
        returnedDto.CommitSha.Should().Be("abc1234");
    }

    [Fact]
    public async Task Create_ValidationError_ReturnsBadRequest()
    {
        _commandHandler.ResultToReturn = new CreateRepositoryWorkspaceResult
        {
            Success = false,
            IsValidationError = true,
            ErrorMessage = "Owner is required.",
        };

        var dto = new CreateRepositoryWorkspaceDto
        {
            Owner = "",
            Repository = "DevPilot",
            Branch = "master",
        };

        var response = await _controller.Create(dto, CancellationToken.None);

        var badRequest = response.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_ConflictError_ReturnsConflict()
    {
        _commandHandler.ResultToReturn = new CreateRepositoryWorkspaceResult
        {
            Success = false,
            IsConflict = true,
            ErrorMessage = "Repository workspace 'enesscigdem/DevPilot' (master) already exists.",
        };

        var dto = new CreateRepositoryWorkspaceDto
        {
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "master",
        };

        var response = await _controller.Create(dto, CancellationToken.None);

        var conflict = response.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Create_OperationalError_Returns500InternalServerError()
    {
        _commandHandler.ResultToReturn = new CreateRepositoryWorkspaceResult
        {
            Success = false,
            IsValidationError = false,
            IsConflict = false,
            ErrorMessage = "Git clone failed: Remote repository not found.",
        };

        var dto = new CreateRepositoryWorkspaceDto
        {
            Owner = "enesscigdem",
            Repository = "NonExistent",
            Branch = "master",
        };

        var response = await _controller.Create(dto, CancellationToken.None);

        var objectResult = response.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task GetById_ExistingWorkspace_ReturnsOkWithDto()
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _dbContext.RepositoryWorkspaces.Add(new RepositoryWorkspace
        {
            Id = id,
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "master",
            LocalPath = "/secret/server/path",
            CommitSha = "sha123",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _dbContext.SaveChangesAsync();

        var response = await _controller.GetById(id, CancellationToken.None);

        var okResult = response.Should().BeOfType<OkObjectResult>().Subject;
        var dto = okResult.Value.Should().BeOfType<RepositoryWorkspaceDto>().Subject;
        dto.Id.Should().Be(id);
        dto.Owner.Should().Be("enesscigdem");
        dto.Repository.Should().Be("DevPilot");
        dto.Branch.Should().Be("master");
        dto.CommitSha.Should().Be("sha123");
        dto.Status.Should().Be(RepositoryWorkspaceStatus.Completed);
    }

    [Fact]
    public async Task GetById_NonExistent_ReturnsNotFound()
    {
        var response = await _controller.GetById(Guid.NewGuid(), CancellationToken.None);

        var notFound = response.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetAll_ReturnsWorkspacesList()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        _dbContext.RepositoryWorkspaces.AddRange(
            new RepositoryWorkspace
            {
                Id = id1,
                Owner = "owner1",
                Repository = "repo1",
                Branch = "main",
                LocalPath = "/path1",
                Status = RepositoryWorkspaceStatus.Completed,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
            },
            new RepositoryWorkspace
            {
                Id = id2,
                Owner = "owner2",
                Repository = "repo2",
                Branch = "dev",
                LocalPath = "/path2",
                Status = RepositoryWorkspaceStatus.Cloning,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        await _dbContext.SaveChangesAsync();

        var response = await _controller.GetAll(CancellationToken.None);

        var okResult = response.Should().BeOfType<OkObjectResult>().Subject;
        var list = okResult.Value.Should().BeAssignableTo<IEnumerable<RepositoryWorkspacesController.RepositoryWorkspaceListDto>>().Subject.ToList();
        list.Should().HaveCount(2);
        list[0].Id.Should().Be(id2);
        list[1].Id.Should().Be(id1);
    }

    private sealed class FakeCreateWorkspaceCommandHandler : ICreateRepositoryWorkspaceCommandHandler
    {
        public CreateRepositoryWorkspaceResult ResultToReturn { get; set; } = new();

        public Task<CreateRepositoryWorkspaceResult> HandleAsync(
            CreateRepositoryWorkspaceCommand command,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResultToReturn);
        }
    }
}
