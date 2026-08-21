using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Services;
using DevPilot.Application.TaskImpactAnalysis.Dtos;
using DevPilot.Application.TaskImpactAnalysis.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure.DatabaseIntelligence;
using Xunit;
using TaskImpactAnalysisEntity = DevPilot.Domain.Entities.TaskImpactAnalysis;

namespace DevPilot.Tests.DatabaseIntelligence;

public sealed class DatabaseMigrationIntelligenceTests
{
    private readonly EfMigrationOperationParser _parser = new();
    private readonly EfCoreDatabaseImpactAnalyzer _analyzer = new();

    #region User Rule 1: Up()-Only Forward Migration Analysis

    [Fact]
    public void Rule1_UpOnlyForwardMigrationAnalysis_IgnoresDownMethodOperations()
    {
        var migrationCode = @"
using Microsoft.EntityFrameworkCore.Migrations;

namespace DevPilot.Infrastructure.Migrations;

public partial class AddDiscountAmount : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: ""DiscountAmount"",
            table: ""Orders"",
            type: ""decimal(18,2)"",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: ""DiscountAmount"",
            table: ""Orders"");

        migrationBuilder.DropTable(
            name: ""Orders"");
    }
}
";
        var changes = _parser.ParseMigrationFile("Migrations/20260821_AddDiscountAmount.cs", migrationCode);

        // Should ONLY have the AddColumn from Up()
        Assert.Single(changes);
        var change = changes[0];
        Assert.Equal(DatabaseObjectType.Column, change.ObjectType);
        Assert.Equal("DiscountAmount", change.ObjectName);
        Assert.Equal("Orders", change.ParentObjectName);
        Assert.Equal(DatabaseChangeOperation.Add, change.Operation);
        Assert.Equal(RiskLevel.Low, change.Risk);

        // Verify that Down()'s DropColumn and DropTable did NOT create destructive forward findings
        Assert.DoesNotContain(changes, c => c.Operation == DatabaseChangeOperation.Remove);
    }

    #endregion

    #region User Rule 2: Non-Nullable Additions / Nullable->Required Without Default

    [Fact]
    public void Rule2_NonNullableAddition_WithoutDefault_ClassifiedAsHighRisk_AndReviewRequired()
    {
        var impactedFiles = new List<ImpactedFile>
        {
            new() { FilePath = "src/DevPilot.Domain/Entities/Customer.cs", ChangeType = ImpactFileChangeType.Modify }
        };

        var evidence = new RepositoryEvidenceProfile
        {
            HasEfCore = true,
            PersistenceFiles = new List<string> { "src/DevPilot.Domain/Entities/Customer.cs" },
            MigrationFiles = new List<string> { "src/DevPilot.Infrastructure/Migrations/20260101_Initial.cs" }
        };

        var impact = _analyzer.AnalyzeImpact(
            impactedFiles,
            new List<ChangeDimensionImpact>(),
            new List<Risk>(),
            evidence,
            taskPrompt: "Add required Email field to Customer",
            workspaceRoot: null);

        Assert.True(impact.RequiresSchemaMigration);
        Assert.Equal(RiskLevel.High, impact.DataRiskLevel);
        Assert.Equal(DataMigrationRequirement.ReviewRequired, impact.DataMigrationRequirement);
        Assert.True(impact.RequiresDataMigration);
        Assert.Contains(impact.Changes, c => c.Risk == RiskLevel.High);
    }

    [Fact]
    public void Rule2_NullableAddition_ClassifiedAsLowRisk_AndNoDataMigrationRequired()
    {
        var impactedFiles = new List<ImpactedFile>
        {
            new() { FilePath = "src/DevPilot.Domain/Entities/Order.cs", ChangeType = ImpactFileChangeType.Modify }
        };

        var evidence = new RepositoryEvidenceProfile
        {
            HasEfCore = true,
            PersistenceFiles = new List<string> { "src/DevPilot.Domain/Entities/Order.cs" }
        };

        var impact = _analyzer.AnalyzeImpact(
            impactedFiles,
            new List<ChangeDimensionImpact>(),
            new List<Risk>(),
            evidence,
            taskPrompt: "Add optional nullable DiscountAmount to Order entity",
            workspaceRoot: null);

        Assert.True(impact.RequiresSchemaMigration);
        Assert.Equal(RiskLevel.Low, impact.DataRiskLevel);
        Assert.Equal(DataMigrationRequirement.None, impact.DataMigrationRequirement);
        Assert.False(impact.RequiresDataMigration);
    }

    #endregion

    #region User Rule 3: Typed DataMigrationRequirement Enum

    [Fact]
    public void Rule3_DataMigrationRequirement_IsTypedEnum()
    {
        var requirements = new[]
        {
            DataMigrationRequirement.None,
            DataMigrationRequirement.ReviewRequired,
            DataMigrationRequirement.Required,
            DataMigrationRequirement.Possible
        };

        Assert.Equal(4, requirements.Length);
        Assert.Equal(DataMigrationRequirement.ReviewRequired, (DataMigrationRequirement)1);
    }

    #endregion

    #region User Rule 4: Structured Intent Without Inventing Migration Filenames

    [Fact]
    public void Rule4_PredictsMigrationExpected_WithoutAuthoritativeFilename()
    {
        var impactedFiles = new List<ImpactedFile>
        {
            new() { FilePath = "src/DevPilot.Domain/Entities/Product.cs", ChangeType = ImpactFileChangeType.Modify }
        };

        var evidence = new RepositoryEvidenceProfile
        {
            HasEfCore = true,
            PersistenceFiles = new List<string> { "src/DevPilot.Domain/Entities/Product.cs" }
        };

        var impact = _analyzer.AnalyzeImpact(
            impactedFiles,
            new List<ChangeDimensionImpact>(),
            new List<Risk>(),
            evidence,
            taskPrompt: "Add SKU property to Product",
            workspaceRoot: null);

        Assert.True(impact.RequiresSchemaMigration);
        Assert.Equal(DatabaseMigrationRequirement.Expected, impact.MigrationRequirement);

        // Changes list contains logical operations, not invented authoritative file paths
        foreach (var change in impact.Changes)
        {
            Assert.False(change.ObjectName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        }
    }

    #endregion

    #region User Rule 5: Normalized Structured Operation Identity Matching

    [Fact]
    public void Rule5_StructuredOperationMatching_MatchesNormalizedTuples()
    {
        var predictedChange = new DatabaseChange
        {
            ObjectType = DatabaseObjectType.Column,
            ObjectName = "DiscountAmount",
            ParentObjectName = "Order",
            Operation = DatabaseChangeOperation.Add,
            Risk = RiskLevel.Low
        };

        var actualChange = new DatabaseChange
        {
            ObjectType = DatabaseObjectType.Column,
            ObjectName = "discountamount",
            ParentObjectName = "Orders", // Plural table vs Singular entity
            Operation = DatabaseChangeOperation.Add,
            Risk = RiskLevel.Low
        };

        Assert.True(predictedChange.Matches(actualChange));
    }

    [Fact]
    public void Rule5_UnexpectedDestructiveOperation_ProducesWarningAndUnexpectedStatus()
    {
        var predictedDbImpact = new DatabaseImpact
        {
            RequiresSchemaMigration = true,
            MigrationRequirement = DatabaseMigrationRequirement.Expected,
            DataRiskLevel = RiskLevel.Low,
            Changes = new List<DatabaseChange>
            {
                new()
                {
                    ObjectType = DatabaseObjectType.Column,
                    ObjectName = "DiscountAmount",
                    ParentObjectName = "Orders",
                    Operation = DatabaseChangeOperation.Add,
                    Risk = RiskLevel.Low
                }
            }
        };

        var actualMigrationCode = @"
using Microsoft.EntityFrameworkCore.Migrations;

public partial class MigrationWithDrop : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(name: ""DiscountAmount"", table: ""Orders"", nullable: true);
        migrationBuilder.DropColumn(name: ""LegacyPrice"", table: ""Orders"");
    }
    protected override void Down(MigrationBuilder migrationBuilder) {}
}
";
        var actualFiles = new List<ExecutionReviewFileDto>
        {
            new("src/DevPilot.Infrastructure/Migrations/20260821_MigrationWithDrop.cs", "Added")
        };

        var comparison = PredictedVsActualEvaluator.EvaluateDatabaseImpact(
            predictedDbImpact,
            actualFiles,
            migrationParser: _parser,
            diff: actualMigrationCode);

        Assert.NotNull(comparison);
        Assert.True(comparison.HasDestructiveOperations);
        Assert.NotEmpty(comparison.DestructiveWarnings);
        Assert.Contains(comparison.DestructiveWarnings, w => w.Contains("LegacyPrice") && w.Contains("DropColumn"));
        Assert.Equal("Unexpected", comparison.Status);
    }

    #endregion

    #region Acceptance Fixture 1: Add Optional Discount Code to Order

    [Fact]
    public void AcceptanceFixture1_OrderDiscountAmount_AdditiveLowRisk()
    {
        var impactedFiles = new List<ImpactedFile>
        {
            new() { FilePath = "src/DevPilot.Domain/Entities/Order.cs", ChangeType = ImpactFileChangeType.Modify }
        };

        var evidence = new RepositoryEvidenceProfile
        {
            HasEfCore = true,
            PersistenceFiles = new List<string> { "src/DevPilot.Domain/Entities/Order.cs" },
            MigrationFiles = new List<string> { "src/DevPilot.Infrastructure/Migrations/20260101_Initial.cs" }
        };

        var impact = _analyzer.AnalyzeImpact(
            impactedFiles,
            new List<ChangeDimensionImpact>(),
            new List<Risk>(),
            evidence,
            taskPrompt: "Add optional DiscountAmount to Order entity",
            workspaceRoot: null);

        Assert.True(impact.RequiresSchemaMigration);
        Assert.Equal(DatabaseChangeKind.Additive, impact.ChangeKind);
        Assert.Equal(RiskLevel.Low, impact.DataRiskLevel);
        Assert.Equal(DataMigrationRequirement.None, impact.DataMigrationRequirement);

        // Actual execution creates migration adding nullable column
        var migrationCode = @"
using Microsoft.EntityFrameworkCore.Migrations;

public partial class AddOrderDiscount : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: ""DiscountAmount"",
            table: ""Orders"",
            type: ""decimal(18,2)"",
            nullable: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder) {}
}
";
        var actualFiles = new List<ExecutionReviewFileDto>
        {
            new("src/DevPilot.Domain/Entities/Order.cs", "Modified"),
            new("src/DevPilot.Infrastructure/Migrations/20260821_AddOrderDiscount.cs", "Added"),
            new("src/DevPilot.Infrastructure/Migrations/AppDbContextModelSnapshot.cs", "Modified")
        };

        var comparison = PredictedVsActualEvaluator.EvaluateDatabaseImpact(
            impact,
            actualFiles,
            migrationParser: _parser,
            diff: migrationCode);

        Assert.NotNull(comparison);
        Assert.True(comparison.ActualMigrationCreated);
        Assert.False(comparison.HasDestructiveOperations);
        Assert.Equal("Matched", comparison.Status);
    }

    #endregion

    #region Acceptance Fixture 2: Customer Email Required with Max Length Reduction

    [Fact]
    public void AcceptanceFixture2_CustomerEmail_Required_And_MaxLengthReduction_HighRisk()
    {
        var impactedFiles = new List<ImpactedFile>
        {
            new() { FilePath = "src/DevPilot.Domain/Entities/Customer.cs", ChangeType = ImpactFileChangeType.Modify }
        };

        var evidence = new RepositoryEvidenceProfile
        {
            HasEfCore = true,
            PersistenceFiles = new List<string> { "src/DevPilot.Domain/Entities/Customer.cs" },
            MigrationFiles = new List<string> { "src/DevPilot.Infrastructure/Migrations/20260101_Initial.cs" }
        };

        var impact = _analyzer.AnalyzeImpact(
            impactedFiles,
            new List<ChangeDimensionImpact>(),
            new List<Risk>(),
            evidence,
            taskPrompt: "Make Customer Email required and reduce max length from 500 to 200 characters",
            workspaceRoot: null);

        Assert.True(impact.RequiresSchemaMigration);
        Assert.Equal(RiskLevel.High, impact.DataRiskLevel);
        Assert.Equal(DataMigrationRequirement.ReviewRequired, impact.DataMigrationRequirement);
        Assert.True(impact.RequiresDataMigration);
        Assert.Contains(impact.Evidence, e => e.Contains("Max length reduction") || e.Contains("Customer"));
    }

    #endregion

    #region Structural & Destructive Operation Tests

    [Fact]
    public void EfMigrationOperationParser_ParsesDropTable_AsDestructiveHighRisk()
    {
        var migrationCode = @"
using Microsoft.EntityFrameworkCore.Migrations;

public partial class RemoveLegacyTables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: ""OldLogs"");
    }
    protected override void Down(MigrationBuilder migrationBuilder) {}
}
";
        var changes = _parser.ParseMigrationFile("Migrations/RemoveLegacyTables.cs", migrationCode);

        Assert.Single(changes);
        var change = changes[0];
        Assert.Equal(DatabaseObjectType.Table, change.ObjectType);
        Assert.Equal("OldLogs", change.ObjectName);
        Assert.Equal(DatabaseChangeOperation.Remove, change.Operation);
        Assert.Equal(RiskLevel.High, change.Risk);
    }

    [Fact]
    public void EfMigrationOperationParser_ParsesCreateIndex_AsMediumRisk()
    {
        var migrationCode = @"
using Microsoft.EntityFrameworkCore.Migrations;

public partial class AddUserEmailIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: ""IX_Users_Email"",
            table: ""Users"",
            column: ""Email"",
            unique: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder) {}
}
";
        var changes = _parser.ParseMigrationFile("Migrations/AddUserEmailIndex.cs", migrationCode);

        Assert.Single(changes);
        var change = changes[0];
        Assert.Equal(DatabaseObjectType.Index, change.ObjectType);
        Assert.Equal("IX_Users_Email", change.ObjectName);
        Assert.Equal("Users", change.ParentObjectName);
        Assert.Equal(DatabaseChangeOperation.Add, change.Operation);
        Assert.Equal(RiskLevel.Medium, change.Risk);
    }

    [Fact]
    public void EfMigrationOperationParser_ParsesAddForeignKey_AsMediumRisk()
    {
        var migrationCode = @"
using Microsoft.EntityFrameworkCore.Migrations;

public partial class AddOrderCustomerFk : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddForeignKey(
            name: ""FK_Orders_Customers_CustomerId"",
            table: ""Orders"",
            column: ""CustomerId"",
            principalTable: ""Customers"",
            principalColumn: ""Id"");
    }
    protected override void Down(MigrationBuilder migrationBuilder) {}
}
";
        var changes = _parser.ParseMigrationFile("Migrations/AddOrderCustomerFk.cs", migrationCode);

        Assert.Single(changes);
        var change = changes[0];
        Assert.Equal(DatabaseObjectType.Relationship, change.ObjectType);
        Assert.Equal("FK_Orders_Customers_CustomerId", change.ObjectName);
        Assert.Equal("Orders", change.ParentObjectName);
        Assert.Equal(DatabaseChangeOperation.Add, change.Operation);
        Assert.Equal(RiskLevel.Medium, change.Risk);
    }

    [Fact]
    public void EfMigrationOperationParser_ParsesRenameColumn_AsAlterOperation()
    {
        var migrationCode = @"
using Microsoft.EntityFrameworkCore.Migrations;

public partial class RenameFieldName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: ""OldName"",
            table: ""Users"",
            newName: ""NewName"");
    }
    protected override void Down(MigrationBuilder migrationBuilder) {}
}
";
        var changes = _parser.ParseMigrationFile("Migrations/RenameFieldName.cs", migrationCode);

        Assert.Single(changes);
        var change = changes[0];
        Assert.Equal(DatabaseObjectType.Column, change.ObjectType);
        Assert.Equal("NewName", change.ObjectName);
        Assert.Equal("Users", change.ParentObjectName);
        Assert.Equal(DatabaseChangeOperation.Rename, change.Operation);
        Assert.Equal("OldName", change.Before);
        Assert.Equal("NewName", change.After);
    }

    [Fact]
    public void EfMigrationOperationParser_ParsesRawSql_AsHighRisk()
    {
        var migrationCode = @"
using Microsoft.EntityFrameworkCore.Migrations;

public partial class CustomDataSeed : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(""UPDATE Orders SET Status = 1 WHERE Status IS NULL"");
    }
    protected override void Down(MigrationBuilder migrationBuilder) {}
}
";
        var changes = _parser.ParseMigrationFile("Migrations/CustomDataSeed.cs", migrationCode);

        Assert.Single(changes);
        var change = changes[0];
        Assert.Equal(DatabaseObjectType.Unknown, change.ObjectType);
        Assert.Equal(DatabaseChangeOperation.Alter, change.Operation);
        Assert.Equal(RiskLevel.High, change.Risk);
        Assert.Contains("Raw SQL", change.Evidence);
    }

    [Fact]
    public void EfMigrationOperationParser_ParsesUnknownOperation_WithoutGuessing()
    {
        var migrationCode = @"
using Microsoft.EntityFrameworkCore.Migrations;

public partial class ExoticOperation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CustomCustomExtensionMethod(""Arg1"");
    }
    protected override void Down(MigrationBuilder migrationBuilder) {}
}
";
        var changes = _parser.ParseMigrationFile("Migrations/ExoticOperation.cs", migrationCode);

        Assert.Single(changes);
        var change = changes[0];
        Assert.Equal(DatabaseObjectType.Unknown, change.ObjectType);
        Assert.Equal(DatabaseChangeOperation.Unknown, change.Operation);
        Assert.Contains("Unsupported or custom migration operation", change.Evidence);
    }

    #endregion

    #region Historical Migration Protection & ModelSnapshot Consequence

    [Fact]
    public void HistoricalMigrations_AreNotTreatedAsNewMigrations()
    {
        var actualFiles = new List<ExecutionReviewFileDto>
        {
            // Historical migration existing from before (Modified or Untouched)
            new("src/DevPilot.Infrastructure/Migrations/20260101_Initial.cs", "Modified"),
            new("src/DevPilot.Domain/Entities/Order.cs", "Modified")
        };

        var comparison = PredictedVsActualEvaluator.EvaluateDatabaseImpact(
            new DatabaseImpact { RequiresSchemaMigration = false },
            actualFiles,
            migrationParser: _parser);

        // Historical migration should not trigger actualMigrationCreated because it was not Added/Created
        Assert.NotNull(comparison);
        Assert.False(comparison.ActualMigrationCreated);
    }

    [Fact]
    public void ModelSnapshot_IsClassifiedAsMigrationConsequence_NotIndependentChange()
    {
        var actualFiles = new List<ExecutionReviewFileDto>
        {
            new("src/DevPilot.Infrastructure/Migrations/20260821_AddColumn.cs", "Added"),
            new("src/DevPilot.Infrastructure/Migrations/AppDbContextModelSnapshot.cs", "Modified")
        };

        var comparison = PredictedVsActualEvaluator.EvaluateDatabaseImpact(
            new DatabaseImpact { RequiresSchemaMigration = true, MigrationRequirement = DatabaseMigrationRequirement.Expected },
            actualFiles,
            migrationParser: _parser);

        Assert.NotNull(comparison);
        Assert.True(comparison.ActualMigrationCreated);
        // ModelSnapshot is excluded from separate migration operation parsing
        Assert.DoesNotContain(comparison.ActualChanges, c => c.ObjectName.Contains("ModelSnapshot"));
    }

    #endregion

    #region Non-Database Task Behavior & Zero Extra Model Calls

    [Fact]
    public void NonDatabaseTask_HasNoDatabaseImpact_AndZeroExtraModelCalls()
    {
        var impactedFiles = new List<ImpactedFile>
        {
            new() { FilePath = "src/DevPilot.Web/src/pages/Dashboard.tsx", ChangeType = ImpactFileChangeType.Modify },
            new() { FilePath = "src/DevPilot.Application/Common/StringHelper.cs", ChangeType = ImpactFileChangeType.Modify }
        };

        var evidence = new RepositoryEvidenceProfile
        {
            HasEfCore = true,
            PersistenceFiles = new List<string> { "src/DevPilot.Domain/Entities/Order.cs", "src/DevPilot.Domain/Entities/Customer.cs" }
        };

        var impact = _analyzer.AnalyzeImpact(
            impactedFiles,
            new List<ChangeDimensionImpact>(),
            new List<Risk>(),
            evidence,
            taskPrompt: "Fix button alignment and string trimming on dashboard",
            workspaceRoot: null);

        Assert.False(impact.RequiresSchemaMigration);
        Assert.Equal(DatabaseMigrationRequirement.None, impact.MigrationRequirement);
        Assert.Equal(DatabaseChangeKind.None, impact.ChangeKind);
        Assert.Equal(RiskLevel.Low, impact.DataRiskLevel);
        Assert.Empty(impact.Changes);
    }

    #endregion
}
