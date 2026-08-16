using DevPilot.Domain.Entities;
using DevPilot.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DevPilot.Tests.Executions;

public class ExecutionActivityModelMappingTests
{
    [Fact]
    public void ExecutionActivity_ModelConfiguration_MatchesRequiredPersistenceMapping()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=test;Password=test", o => o.UseVector())
            .Options;

        using var db = new DevPilotDbContext(options);
        var entityType = db.Model.FindEntityType(typeof(ExecutionActivity));

        entityType.Should().NotBeNull();

        // Stage stored as string / varchar(50)
        var stageProp = entityType!.FindProperty(nameof(ExecutionActivity.Stage));
        stageProp.Should().NotBeNull();
        stageProp!.GetProviderClrType().Should().Be(typeof(string));
        stageProp.GetMaxLength().Should().Be(50);

        // Status stored as string / varchar(50)
        var statusProp = entityType.FindProperty(nameof(ExecutionActivity.Status));
        statusProp.Should().NotBeNull();
        statusProp!.GetProviderClrType().Should().Be(typeof(string));
        statusProp.GetMaxLength().Should().Be(50);

        // Message max length 500
        var messageProp = entityType.FindProperty(nameof(ExecutionActivity.Message));
        messageProp.Should().NotBeNull();
        messageProp!.GetMaxLength().Should().Be(500);

        // MetadataJson column type jsonb
        var metadataProp = entityType.FindProperty(nameof(ExecutionActivity.MetadataJson));
        metadataProp.Should().NotBeNull();
        metadataProp!.GetColumnType().Should().Be("jsonb");

        // Composite index (ExecutionId, CreatedAt, Id) with name IX_ExecutionActivities_ExecutionId_CreatedAt_Id
        var index = entityType.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "IX_ExecutionActivities_ExecutionId_CreatedAt_Id");
        index.Should().NotBeNull();
        index!.Properties.Select(p => p.Name).Should().Equal(
            nameof(ExecutionActivity.ExecutionId),
            nameof(ExecutionActivity.CreatedAt),
            nameof(ExecutionActivity.Id)
        );
    }
}
