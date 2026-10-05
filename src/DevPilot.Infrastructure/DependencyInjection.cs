using DevPilot.Application.AiProviders;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.GitProviders;
using DevPilot.Application.ModelComparisons;
using DevPilot.Application.ProjectBrain.Commands.AskBrain;
using DevPilot.Application.ProjectBrain.Commands.IndexWorkspace;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.ProjectBrain.Queries.GetBrainStatus;
using DevPilot.Application.ProjectBrain.Queries.SemanticSearch;
using DevPilot.Application.RepositoryClone;
using DevPilot.Application.RepositoryWorkspaces.Commands.CreateRepositoryWorkspace;
using DevPilot.Application.RepositoryWorkspaces.Ports;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetRepositoryWorkspaceAnalysis;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetRepositoryWorkspaceArchitecture;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetWorkspaceInsights;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetWorkspaceOverview;
using DevPilot.Infrastructure.RepositoryInspection;
using DevPilot.Infrastructure.RepositoryWorkspaces;
using DevPilot.Application.TaskImpactAnalysis.Commands.AnalyzeTaskImpact;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.TaskImpactAnalysis.Queries.GetTaskImpactAnalysis;
using DevPilot.Application.Tasks.Commands.ApproveTask;
using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Commands.CommitExecution;
using DevPilot.Application.Executions.Commands.PushExecution;
using DevPilot.Application.Executions.Commands.CreatePullRequest;
using DevPilot.Application.Executions.Commands.SyncPullRequest;
using DevPilot.Application.Executions.Commands.ProcessExecution;
using DevPilot.Application.Executions.Commands.RejectExecutionReview;
using DevPilot.Application.Executions.Commands.RunDeveloperAgent;
using DevPilot.Application.Executions.Commands.StartExecution;
using DevPilot.Application.Executions.Commands.RetryExecution;
using DevPilot.Application.Executions.Commands.MergeExecution;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Services;
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
using DevPilot.Infrastructure.ModelComparisons;
using DevPilot.Infrastructure.Tasks;
using DevPilot.Infrastructure.Executions;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Infrastructure.DeveloperAgent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pgvector.EntityFrameworkCore;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DevPilot.Tests")]

namespace DevPilot.Infrastructure;

public static class DependencyInjection
{
    private const string LegacyAiProviderKey = "legacy-ai-provider";

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

        services.Configure<FrontendOptions>(configuration.GetSection(FrontendOptions.SectionName));
        services.AddAiProviders(configuration);
        services.AddGitProviders(configuration);
        services.AddRepositoryClone(configuration);
        services.AddProjectBrain();
        services.AddScoped<IRepositoryAnalyzer, RoslynRepositoryAnalyzer>();
        services.AddTask(configuration);

        return services;
    }

    private static IServiceCollection AddTask(this IServiceCollection services, IConfiguration configuration)
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
        services.AddScoped<EfExecutionRepository>();
        services.AddScoped<IExecutionRepository>(sp => sp.GetRequiredService<EfExecutionRepository>());
        services.AddScoped<IExecutionVerificationRerunStore>(sp => sp.GetRequiredService<EfExecutionRepository>());
        services.AddScoped<IExecutionRevisionStore>(sp => sp.GetRequiredService<EfExecutionRepository>());
        services.AddScoped<IExecutionWorktreeSnapshotService, GitWorktreeSnapshotService>();
        services.AddScoped<IExecutionListReader, EfExecutionListReader>();
        services.AddScoped<IExecutionWorkspaceManager, GitExecutionWorkspaceManager>();
        // Single authoritative reliability configuration shared by the processor, DeveloperAgent and impact analysis.
        services.AddSingleton(_ => ExecutionReliabilityOptionsFactory.Create(configuration));
        // Prices set on models in the panel are read once per scope, so a price edit applies to the next
        // request or execution; the optional global AiPricing section remains the fallback.
        services.AddScoped(sp => AiPricingOptionsFactory.Build(
            configuration.GetSection(AiPricingOptions.SectionName),
            sp.GetRequiredService<DevPilotDbContext>()));
        services.AddScoped<IExecutionVerificationSnapshotStore, EfExecutionVerificationSnapshotStore>();
        services.AddScoped<IExecutionVerificationSnapshotRecorder, ExecutionVerificationSnapshotRecorder>();
        services.AddScoped<IExecutionProcessor, GitWorkspaceExecutionProcessor>();
        services.AddScoped<IExecutionDispatcher, HangfireExecutionDispatcher>();
        services.AddScoped<IExecutionRevisionDispatcher, HangfireExecutionDispatcher>();
        services.AddScoped<IWorktreeEditApplier, WorktreeEditApplier>();
        services.AddScoped<IDeveloperAgent, DevPilot.Infrastructure.DeveloperAgent.DeveloperAgent>();
        services.AddScoped<IAgenticRepairService, DevPilot.Infrastructure.DeveloperAgent.AgenticRepairService>();
        services.AddScoped<IReviewFeedbackAgent, DevPilot.Infrastructure.DeveloperAgent.DeveloperAgent>();
        services.AddScoped<IProcessRunner, BoundedProcessRunner>();
        services.AddScoped<IVisualCaptureService, DevPilot.Infrastructure.Executions.Visual.VisualCaptureService>();
        services.AddScoped<IVisualArtifactReader, DevPilot.Infrastructure.Executions.Visual.VisualArtifactStore>();
        services.AddScoped<IRepositoryCheckRunner, RepositoryNativeCheckRunner>();
        services.AddScoped<IRepositoryRepairContextProvider, DotNetRepositoryRepairContextProvider>();
        services.AddScoped<IStartExecutionCommandHandler, StartExecutionCommandHandler>();
        services.AddScoped<IRetryExecutionCommandHandler, RetryExecutionCommandHandler>();
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
        services.AddScoped<IExecutionGitPushService, GitExecutionPushService>();
        services.AddScoped<IPushExecutionCommandHandler, PushExecutionCommandHandler>();
        services.AddScoped<IExecutionGitHubPullRequestService, GitHubExecutionPullRequestService>();
        services.AddScoped<IExecutionGitHubSyncService, ExecutionGitHubSyncService>();
        services.AddScoped<ICreatePullRequestCommandHandler, CreatePullRequestCommandHandler>();
        services.AddScoped<ISyncPullRequestCommandHandler, SyncPullRequestCommandHandler>();
        services.AddSingleton<IBaselineVerificationCoordinator, BaselineVerificationCoordinator>();
        services.AddScoped<IBaselineVerificationService, BaselineVerificationService>();
        services.AddSingleton<IExecutionCancellationRegistry, ExecutionCancellationRegistry>();
        services.AddSingleton<IExecutionHeartbeatService, ExecutionHeartbeatService>();
        services.AddHostedService<ExecutionStartupReconciler>();
        services.AddSingleton<DevPilot.Application.Automation.AutomationDecisionLedger>();
        services.AddScoped<DevPilot.Application.Automation.IAutomationPolicyStore, DevPilot.Infrastructure.Automation.EfAutomationPolicyStore>();
        services.AddScoped<DevPilot.Application.Automation.IAutomationWorkReader, DevPilot.Infrastructure.Automation.EfAutomationWorkReader>();
        services.AddScoped<DevPilot.Application.Automation.IAutomationOrchestrator, DevPilot.Application.Automation.AutomationOrchestrator>();
        services.AddHostedService<DevPilot.Infrastructure.Automation.AutomationWorker>();
        services.AddScoped<DevPilot.Application.Executions.Commands.CancelExecution.ICancelExecutionCommandHandler, DevPilot.Application.Executions.Commands.CancelExecution.CancelExecutionCommandHandler>();
        services.AddScoped<DevPilot.Application.Executions.Commands.VerifyExecution.IVerifyExecutionCommandHandler, DevPilot.Application.Executions.Commands.VerifyExecution.VerifyExecutionCommandHandler>();
        services.AddScoped<DevPilot.Application.Executions.Commands.RequestExecutionChanges.IRequestExecutionChangesCommandHandler, DevPilot.Application.Executions.Commands.RequestExecutionChanges.RequestExecutionChangesCommandHandler>();
        services.AddScoped<DevPilot.Application.Executions.Queries.GetExecutionRevisionDiff.IGetExecutionRevisionDiffQueryHandler, DevPilot.Application.Executions.Queries.GetExecutionRevisionDiff.GetExecutionRevisionDiffQueryHandler>();
        services.AddScoped<IApproveExecutionReviewCommandHandler, ApproveExecutionReviewCommandHandler>();
        services.AddScoped<IRejectExecutionReviewCommandHandler, RejectExecutionReviewCommandHandler>();
        services.AddScoped<IMergeExecutionCommandHandler, MergeExecutionCommandHandler>();
        services.Configure<MergePolicyOptions>(configuration.GetSection("MergePolicy"));

        return services;
    }

    private static IServiceCollection AddRepositoryClone(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RepositoryCloneOptions>(
            configuration.GetSection(RepositoryCloneOptions.SectionName));

        services.AddScoped<IRepositoryCloneService, RepositoryCloneService>();
        services.AddScoped<IRepositoryFreshnessService, GitRepositoryFreshnessService>();
        services.AddScoped<ICreateRepositoryWorkspaceCommandHandler, CreateRepositoryWorkspaceCommandHandler>();
        services.AddScoped<IRepositoryStructureScanner, RepositoryStructureScanner>();
        services.AddScoped<IGetRepositoryWorkspaceAnalysisQueryHandler, GetRepositoryWorkspaceAnalysisQueryHandler>();
        services.AddScoped<IGetRepositoryWorkspaceArchitectureQueryHandler, GetRepositoryWorkspaceArchitectureQueryHandler>();
        services.AddScoped<IWorkspaceOverviewReader, EfWorkspaceOverviewReader>();
        services.AddScoped<IWorkspaceInsightsReader, EfWorkspaceInsightsReader>();
        services.AddScoped<IGetWorkspaceInsightsQueryHandler, GetWorkspaceInsightsQueryHandler>();
        services.AddScoped<IGetWorkspaceOverviewQueryHandler, GetWorkspaceOverviewQueryHandler>();

        return services;
    }

    private static IServiceCollection AddAiProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var providerName = configuration["AiProvider:Provider"] ?? string.Empty;

        services.AddHttpClient(OpenAiCompatibleProvider.HttpClientName, client =>
        {
            // The provider enforces its own, reported deadlines (total and stream idle); this is only a safety net above them.
            client.Timeout = TimeSpan.FromSeconds(330);
        });

        // API keys entered in the panel are encrypted at rest. The key ring must survive restarts
        // (mount a volume in containers) or stored keys can no longer be decrypted.
        var keyRingPath = configuration["AiModels:KeyRingPath"];
        if (string.IsNullOrWhiteSpace(keyRingPath))
        {
            keyRingPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevPilot",
                "keys");
        }

        services.AddDataProtection()
            .SetApplicationName("DevPilot")
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
        services.AddSingleton<IAiKeyProtector, AiKeyProtector>();
        services.AddSingleton<IAiProviderFactory, AiProviderFactory>();
        services.AddScoped<IAiModelService, AiModelService>();
        services.AddScoped<IAiModelDiscoveryService, AiModelDiscoveryService>();

        // The router is the IAiProvider the app uses; models added in the panel take precedence
        // and the appsettings provider below is the fallback when none are configured.
        services.AddScoped<IAiExecutionContext, AiExecutionContext>();
        services.AddScoped<IAiProvider>(sp => new RoutingAiProvider(
            sp.GetRequiredService<DevPilotDbContext>(),
            sp.GetRequiredService<IAiKeyProtector>(),
            sp.GetRequiredService<IAiProviderFactory>(),
            sp.GetRequiredKeyedService<IAiProvider>(LegacyAiProviderKey),
            sp.GetRequiredService<IAiExecutionContext>()));

        services.AddScoped<IModelComparisonService, ModelComparisonService>();
        services.AddHostedService<ModelComparisonCoordinator>();

        // Models added in the panel are the primary path. The appsettings Kimi provider stays as a
        // fallback for existing installs; any other value falls back to a provider that fails loudly.
        if (providerName == AiProviderNames.Kimi)
        {
            services.AddKeyedScoped<IAiProvider, KimiAiProvider>(LegacyAiProviderKey);
        }
        else
        {
            services.AddKeyedScoped<IAiProvider, UnconfiguredAiProvider>(LegacyAiProviderKey);
        }

        return services;
    }

    private static IServiceCollection AddGitProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var providerName = configuration["GitProvider:Provider"] ?? string.Empty;

        services.Configure<GitHubAppOptions>(configuration.GetSection(GitHubAppOptions.SectionName));
        services.AddSingleton<IGitHubOAuthStateService, GitHubOAuthStateService>();
        services.AddScoped<IGitHubAppTokenService, GitHubAppTokenService>();

        switch (providerName)
        {
            case GitProviderNames.GitHub:
                services.AddHttpClient(GitHubGitProvider.HttpClientName, client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                });
                services.AddHttpClient(GitHubPullRequestClient.HttpClientName, client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                });
                services.AddScoped<IGitProvider, GitHubGitProvider>();
                services.AddScoped<IGitHubPullRequestClient, GitHubPullRequestClient>();
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
        services.AddScoped<IProjectBrainConversationRepository, EfProjectBrainConversationRepository>();
        services.AddScoped<IIndexWorkspaceCommandHandler, IndexWorkspaceCommandHandler>();
        services.AddScoped<ISemanticSearchQueryHandler, SemanticSearchQueryHandler>();
        services.AddScoped<ISemanticSearchService, EfSemanticSearchService>();
        services.AddScoped<IGetBrainStatusQueryHandler, GetBrainStatusQueryHandler>();
        services.AddScoped<IAskBrainCommandHandler, AskBrainCommandHandler>();
        services.AddScoped<DevPilot.Application.ProjectBrain.Queries.GetBrainConversations.IGetBrainConversationsQueryHandler, DevPilot.Application.ProjectBrain.Queries.GetBrainConversations.GetBrainConversationsQueryHandler>();
        services.AddScoped<DevPilot.Application.ProjectBrain.Queries.GetBrainConversationById.IGetBrainConversationByIdQueryHandler, DevPilot.Application.ProjectBrain.Queries.GetBrainConversationById.GetBrainConversationByIdQueryHandler>();
        services.AddScoped<DevPilot.Application.ProjectBrain.Commands.CreateBrainConversation.ICreateBrainConversationCommandHandler, DevPilot.Application.ProjectBrain.Commands.CreateBrainConversation.CreateBrainConversationCommandHandler>();
        services.AddScoped<DevPilot.Application.ProjectBrain.Commands.DeleteBrainConversation.IDeleteBrainConversationCommandHandler, DevPilot.Application.ProjectBrain.Commands.DeleteBrainConversation.DeleteBrainConversationCommandHandler>();

        return services;
    }
}
