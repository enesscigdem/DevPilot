using System.Reflection;
using DevPilot.Api.Controllers;
using DevPilot.Application.Tasks.Batch;
using DevPilot.Application.Tasks.Commands.CreateTask;
using DevPilot.Application.Tasks.Dtos;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace DevPilot.Tests.Tasks;

/// <summary>Guards the HTTP surface of the batch feature: the routes exist and the actions answer as the UI expects.</summary>
public class TaskBatchEndpointTests
{
    private static string? RouteOf(string action) =>
        typeof(TasksController).GetMethod(action)!.GetCustomAttribute<HttpPostAttribute>()?.Template;

    [Fact]
    public void Batch_routes_are_registered()
    {
        RouteOf(nameof(TasksController.ParseTaskBatch)).Should().Be("batch/parse");
        RouteOf(nameof(TasksController.CreateTaskBatch)).Should().Be("batch");
        typeof(TasksController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/tasks");
    }

    [Fact]
    public void Parse_returns_the_drafts()
    {
        var controller = NewController(new RecordingCreate());

        var result = controller.ParseTaskBatch(new ParseTaskBatchRequest("Task: A\nDescription: a\n\nTask: B\nDescription: b"));

        var parsed = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<TaskBatchParseResult>().Subject;
        parsed.Drafts.Select(d => d.Title).Should().Equal("A", "B");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Parse_rejects_empty_text(string? text)
    {
        NewController(new RecordingCreate()).ParseTaskBatch(new ParseTaskBatchRequest(text))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_returns_one_result_per_task_even_when_one_fails()
    {
        var create = new RecordingCreate();
        var controller = NewController(create);

        var result = await controller.CreateTaskBatch(
            new CreateTaskBatchRequest
            {
                RepositoryWorkspaceId = Guid.NewGuid(),
                Tasks = new List<BatchTaskItem>
                {
                    new() { Title = "ok", Description = "d" },
                    new() { Title = "", Description = "d" }
                }
            },
            CancellationToken.None);

        var body = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<CreateTaskBatchResult>().Subject;
        body.Items.Select(i => i.Success).Should().Equal(true, false);
    }

    [Fact]
    public async Task Create_without_tasks_is_a_bad_request()
    {
        var result = await NewController(new RecordingCreate()).CreateTaskBatch(
            new CreateTaskBatchRequest { RepositoryWorkspaceId = Guid.NewGuid(), Tasks = null },
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // Only the batch handler is used by these actions; the other handlers are never touched.
    private static TasksController NewController(ICreateTaskCommandHandler create) =>
        new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            new CreateTaskBatchCommandHandler(create));

    private sealed class RecordingCreate : ICreateTaskCommandHandler
    {
        public Task<CreateTaskResult> HandleAsync(CreateTaskCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.IsNullOrWhiteSpace(command.Dto.Title)
                ? new CreateTaskResult { Success = false, ErrorMessage = "Title is required." }
                : new CreateTaskResult { Success = true, Task = new TaskDto { Id = Guid.NewGuid(), Title = command.Dto.Title } });
    }
}
