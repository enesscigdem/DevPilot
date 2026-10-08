namespace DevPilot.Domain.Enums;

public enum GitProviderKind
{
    GitHub = 0,
    GitLab = 1,

    /// <summary>Any HTTPS git remote. DevPilot can clone and push but has no pull request API.</summary>
    Generic = 2,

    /// <summary>Azure DevOps Services (dev.azure.com). Owner is organization/project.</summary>
    AzureDevOps = 3,

    /// <summary>Bitbucket Cloud (bitbucket.org). Owner is the workspace.</summary>
    Bitbucket = 4,
}
