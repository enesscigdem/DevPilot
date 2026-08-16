using DevPilot.Application.AiProviders;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.GitProviders;
using DevPilot.Application.ProjectBrain.Commands.IndexWorkspace;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.ProjectBrain.Queries.SemanticSearch;
using DevPilot.Application.RepositoryClone;
using DevPilot.Application.TaskImpactAnalysis.Commands.AnalyzeTaskImpact;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.TaskImpactAnalysis.Queries.GetTaskImpactAnalysis;
using DevPilot.Application.Tasks.Commands.ApproveTask;
using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Commands.CommitExecution;
using DevPilot.Application.Executions.Commands.ProcessExecution;
using DevPilot.Application.Executions.Commands.RejectExecutionReview;
using DevPilot.Application.Executions.Commands.RunDeveloperAgent;
using DevPilot.Application.Executions.Commands.StartExecution;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Queries.GetExecutionById;
using DevPilot.Application.Executions.Queries.GetExecutionReview;
using DevPilot.Application.Executions.Queries.GetExecutionActivity;
using DevPilot.Application.Executions.Queries.GetExecutions;
using DevPilot.Application.Tasks.Commands.CreateTask;
using DevPilot.Application.Tasks.Commands.DeleteTask;
using DevPilot.Application.Tasks.Commands.RejectTask;
using DevPilot.Application.Tasks.Commands.UpdateTask;
using DevPilot.Application.Tasks.Commands.UpdateTaskStatus;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Application.Tasks.Queries.GetTaskById;
using DevPilot.Application.Tasks.Queries.GetTasks;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Infrastructure.AiProviders;
using DevPilot.Infrastructure.CodeAnalysis;
using DevPilot.Infrastructure.GitProviders;
using DevPilot.Infrastructure.ProjectBrain;
using DevPilot.Infrastructure.ProjectBrain.EmbeddingProviders;
using DevPilot.Infrastructure.ProjectBrain.Repositories;
using DevPilot.Infrastructure.ProjectBrain.SemanticSearch;
using DevPilot.Infrastructure.RepositoryClone;
using DevPilot.Infrastructure.ImpactAnalysis;
using DevPilot.Infrastructure.Tasks;
using DevPilot.Infrastructure.Executions;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Infrastructure.DeveloperAgent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pgvector.EntityFrameworkCore;

namespace DevPilot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DevPilotDb")
            ?? throw new InvalidOperationException("Connection string 'DevPilotDb' is not configured.");

        services.AddDbContext<DevPilotDbContext>(options =>
        options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseVector();
            npgsql.MigrationsAssembly(typeof(DependencyInjection).Assembly.FullName);
        }));

        services.AddAiProviders(configuration);
        services.AddGitProviders(configuration);
        services.AddRepositoryClone(configuration);
        services.AddProjectBrain();
        services.AddScoped<IRepositoryAnalyzer, RoslynRepositoryAnalyzer>();
        services.AddTask();

        return services;
    }

    private static IServiceCollection AddTask(this IServiceCollection services)
    {
        services.AddScoped<ITaskRepository, EfTaskRepository>();
        services.AddScoped<IRepositoryWorkspaceQuery, RepositoryWorkspaceQuery>();
        services.AddScoped<IImpactAnalysisRepository, EfImpactAnalysisRepository>();
        services.AddScoped<ICreateTaskCommandHandler, CreateTaskCommandHandler>();
        services.AddScoped<IUpdateTaskCommandHandler, UpdateTaskCommandHandler>();
        services.AddScoped<IUpdateTaskStatusCommandHandler, UpdateTaskStatusCommandHandler>();
        services.AddScoped<IDeleteTaskCommandHandler, DeleteTaskCommandHandler>();
        services.AddScoped<IGetTaskByIdQueryHandler, GetTaskByIdQueryHandler>();
        services.AddScoped<IGetTasksQueryHandler, GetTasksQueryHandler>();
        services.AddScoped<IAnalyzeTaskImpactCommandHandler, AnalyzeTaskImpactCommandHandler>();
        services.AddScoped<IGetTaskImpactAnalysisQueryHandler, GetTaskImpactAnalysisQueryHandler>();
        services.AddScoped<IApproveTaskCommandHandler, ApproveTaskCommandHandler>();
        services.AddScoped<IRejectTaskCommandHandler, RejectTaskCommandHandler>();
        services.AddScoped<IExecutionRepository, EfExecutionRepository>();
        services.AddScoped<IExecutionWorkspaceManager, GitExecutionWorkspaceManager>();
        services.AddScoped<IExecutionProcessor, GitWorkspaceExecutionProcessor>();
        services.AddScoped<IExecutionDispatcher, HangfireExecutionDispatcher>();
        services.AddScoped<IWorktreeEditApplier, WorktreeEditApplier>();
        services.AddScoped<IDeveloperAgent, DevPilot.Infrastructure.DeveloperAgent.DeveloperAgent>();
        services.AddScoped<IProcessRunner, DotnetProcessRunner>();
        services.AddScoped<IExecutionValidationRunner, DotnetExecutionValidationRunner>();
        services.AddScoped<IStartExecutionCommandHandler, StartExecutionCommandHandler>();
        services.AddScoped<IProcessExecutionCommandHandler, ProcessExecutionCommandHandler>();
        services.AddScoped<ExecutionWorkerJob>();
        services.AddScoped<IGetExecutionByIdQueryHandler, GetExecutionByIdQueryHandler>();
        services.AddScoped<IGetExecutionsQueryHandler, GetExecutionsQueryHandler>();
        services.AddScoped<IRunDeveloperAgentCommandHandler, RunDeveloperAgentCommandHandler>();
        services.AddScoped<IExecutionGitDiffReader, GitExecutionDiffReader>();
        services.AddScoped<IGetExecutionReviewQueryHandler, GetExecutionReviewQueryHandler>();
        services.AddScoped<IExecutionActivityRecorder, EfExecutionActivityRecorder>();
        services.AddScoped<IExecutionActivityRepository, EfExecutionActivityRepository>();
        services.AddScoped<IGetExecutionActivityQueryHandler, GetExecutionActivityQueryHandler>();
        services.AddScoped<IExecutionChangeFingerprintCalculator, GitExecutionChangeFingerprintCalculator>();
        services.AddScoped<IExecutionGitCommitService, GitExecutionCommitService>();
        services.AddScoped<ICommitExecutionCommandHandler, CommitExecutionCommandHandler>();
        services.AddScoped<IApproveExecutionReviewCommandHandler, ApproveExecutionReviewCommandHandler>();
        services.AddScoped<IRejectExecutionReviewCommandHandler, RejectExecutionReviewCommandHandler>();

        return services;
    }

    private static IServiceCollection AddRepositoryClone(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RepositoryCloneOptions>(
            configuration.GetSection(RepositoryCloneOptions.SectionName));

        services.AddScoped<IRepositoryCloneService, RepositoryCloneService>();

        return services;
    }

    private static IServiceCollection AddAiProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var providerName = configuration["AiProvider:Provider"] ?? string.Empty;

        if (providerName == AiProviderNames.Kimi)
        {
            services.AddHttpClient("Kimi", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(300);
            });
        }

        switch (providerName)
        {
            case AiProviderNames.Kimi:
                services.AddScoped<IAiProvider, KimiAiProvider>();
                break;
            case AiProviderNames.OpenAI:
                services.AddScoped<IAiProvider, OpenAiAiProvider>();
                break;
            case AiProviderNames.Claude:
                services.AddScoped<IAiProvider, ClaudeAiProvider>();
                break;
            case AiProviderNames.Gemini:
                services.AddScoped<IAiProvider, GeminiAiProvider>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported AI provider '{providerName}'. Supported providers: {AiProviderNames.Kimi}, {AiProviderNames.OpenAI}, {AiProviderNames.Claude}, {AiProviderNames.Gemini}.");
        }

        return services;
    }

    private static IServiceCollection AddGitProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var providerName = configuration["GitProvider:Provider"] ?? string.Empty;

        switch (providerName)
        {
            case GitProviderNames.GitHub:
                services.AddHttpClient(GitHubGitProvider.HttpClientName, client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                });
                services.AddScoped<IGitProvider, GitHubGitProvider>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported Git provider '{providerName}'. Supported providers: {GitProviderNames.GitHub}.");
        }

        return services;
    }

    private static IServiceCollection AddProjectBrain(this IServiceCollection services)
    {
        services.AddScoped<IRepositoryChunker, RepositoryChunker>();
        services.AddScoped<IEmbeddingProvider, NullEmbeddingProvider>();
        services.AddScoped<ICodeChunkRepository, EfCodeChunkRepository>();
        services.AddScoped<IIndexJobRepository, EfIndexJobRepository>();
        services.AddScoped<IIndexWorkspaceCommandHandler, IndexWorkspaceCommandHandler>();
        services.AddScoped<ISemanticSearchQueryHandler, SemanticSearchQueryHandler>();
        services.AddScoped<ISemanticSearchService, NullSemanticSearchService>();

        return services;
    }
}
