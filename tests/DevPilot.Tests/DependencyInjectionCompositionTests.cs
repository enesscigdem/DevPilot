using DevPilot.Application.Executions.Commands.ProcessExecution;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Queries.GetExecutionReview;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevPilot.Tests;

public class DependencyInjectionCompositionTests
{
    [Fact]
    public void BuildServiceProvider_WithValidateScopesAndValidateOnBuild_SucceedsWithoutLifetimeMismatch()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DevPilotDb"] = "Host=localhost;Database=devpilot;Username=postgres;Password=postgres",
                ["AiProvider:Provider"] = "OpenAI",
                ["AiProvider:ApiKey"] = "fake-api-key",
                ["GitProvider:Provider"] = "GitHub",
                ["GitProvider:Token"] = "fake-github-token"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<Hangfire.IBackgroundJobClient, MockBackgroundJobClient>();
        services.AddInfrastructure(configuration);

        // Build with strict DI container validation: ValidateScopes and ValidateOnBuild enabled
        var providerOptions = new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        };

        var buildAction = () => services.BuildServiceProvider(providerOptions);
        buildAction.Should().NotThrow("DI composition must build cleanly under strict scope and lifetime validation");

        using var serviceProvider = services.BuildServiceProvider(providerOptions);

        // Singleton coordinator resolves from root container
        var coordinator = serviceProvider.GetService<IBaselineVerificationCoordinator>();
        coordinator.Should().NotBeNull();

        // Scoped baseline service and execution handlers resolve within a scope without lifetime mismatch
        using var scope = serviceProvider.CreateScope();

        var baselineService = scope.ServiceProvider.GetService<IBaselineVerificationService>();
        baselineService.Should().NotBeNull();
        baselineService.Should().BeOfType<BaselineVerificationService>();

        var checkRunner = scope.ServiceProvider.GetService<IRepositoryCheckRunner>();
        checkRunner.Should().NotBeNull();

        var processExecutionHandler = scope.ServiceProvider.GetService<IProcessExecutionCommandHandler>();
        processExecutionHandler.Should().NotBeNull();

        var getReviewHandler = scope.ServiceProvider.GetService<IGetExecutionReviewQueryHandler>();
        getReviewHandler.Should().NotBeNull();
    }

    private sealed class MockBackgroundJobClient : Hangfire.IBackgroundJobClient
    {
        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) => true;
        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state) => "mock-job-id";
    }
}
