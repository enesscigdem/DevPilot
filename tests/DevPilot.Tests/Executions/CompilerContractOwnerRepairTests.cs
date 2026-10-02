using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class CompilerContractOwnerRepairTests
{
    [Fact]
    public void SharedMissingContract_AcrossTwoConsumerDiagnostics_SelectsUniqueTouchedOwner()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
            """
            src/repository/entityRepository.ts(3,18): error TS2339: Property 'extraField' does not exist on type 'CreateEntityInput'.
            src/services/entityService.ts(4,20): error TS2339: Property 'extraField' does not exist on type 'CreateEntityInput'.
            """,
            null,
            "tsc failed");

        var sources = new Dictionary<string, string>
        {
            ["src/models/entity.ts"] = "export interface CreateEntityInput { title: string; }",
            ["src/repository/entityRepository.ts"] = "import { CreateEntityInput } from '../models/entity';\nexport function create(input: CreateEntityInput) { return input.extraField; }",
            ["src/services/entityService.ts"] = "import { CreateEntityInput } from '../models/entity';\nexport function save(input: CreateEntityInput) { return input.extraField; }"
        };

        var shared = ExecutionDiagnosticEvidence.TryGetSharedMissingContract(evidence.DiagnosticLines);
        shared.Should().NotBeNull();
        shared!.TypeName.Should().Be("CreateEntityInput");
        shared.MemberName.Should().Be("extraField");

        var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
            evidence,
            sources.Keys,
            touchedFileContents: sources);

        selection.FilePath.Should().Be("src/models/entity.ts");
        selection.Reason.Should().Be("UniqueContractOwner");
        ExecutionDiagnosticEvidence.SelectCompilerRepairFiles(evidence, sources.Keys)
            .Should().Equal("src/repository/entityRepository.ts", "src/services/entityService.ts");
    }

    [Fact]
    public void AmbiguousContractOwnership_DoesNotGuess()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
            """
            src/repository/entityRepository.ts(3,18): error TS2339: Property 'extraField' does not exist on type 'CreateEntityInput'.
            src/services/entityService.ts(4,20): error TS2339: Property 'extraField' does not exist on type 'CreateEntityInput'.
            """,
            null,
            "tsc failed");

        var sources = new Dictionary<string, string>
        {
            ["src/models/entity.ts"] = "export interface CreateEntityInput { title: string; }",
            ["src/models/entityCompat.ts"] = "export interface CreateEntityInput { title: string; }",
            ["src/repository/entityRepository.ts"] = "export function create(input: CreateEntityInput) { return input.extraField; }",
            ["src/services/entityService.ts"] = "export function save(input: CreateEntityInput) { return input.extraField; }"
        };

        ExecutionDiagnosticEvidence.TrySelectUniqueSharedContractOwner(evidence, sources.Keys, sources)
            .Should().BeNull();

        var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
            evidence,
            sources.Keys,
            touchedFileContents: sources);

        selection.Reason.Should().Be("NextUnattemptedFile");
        selection.FilePath.Should().Be("src/repository/entityRepository.ts");
    }

    [Fact]
    public void UnparseableDiagnostics_FallBackToExactDiagnosticFile()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
            """
            src/repository/entityRepository.ts(3,18): error TS2322: Type 'string' is not assignable to type 'boolean'.
            src/services/entityService.ts(4,20): error TS2322: Type 'string' is not assignable to type 'boolean'.
            """,
            null,
            "tsc failed");

        var sources = new Dictionary<string, string>
        {
            ["src/models/entity.ts"] = "export interface CreateEntityInput { title: string; }",
            ["src/repository/entityRepository.ts"] = "export const x: boolean = 'no';",
            ["src/services/entityService.ts"] = "export const y: boolean = 'no';"
        };

        ExecutionDiagnosticEvidence.TryGetSharedMissingContract(evidence.DiagnosticLines).Should().BeNull();
        var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
            evidence,
            sources.Keys,
            touchedFileContents: sources);
        selection.FilePath.Should().Be("src/repository/entityRepository.ts");
        selection.Reason.Should().Be("NextUnattemptedFile");
    }

    [Fact]
    public void CsharpMissingDefinition_CanIdentifyUniqueOwner()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
            """
            src/Repos/EntityRepository.cs(10,20): error CS0117: 'CreateEntityInput' does not contain a definition for 'ExtraField'
            src/Services/EntityService.cs(12,20): error CS0117: 'CreateEntityInput' does not contain a definition for 'ExtraField'
            """,
            null,
            "dotnet build failed");

        var sources = new Dictionary<string, string>
        {
            ["src/Models/Entity.cs"] = "public sealed record CreateEntityInput(string Title);",
            ["src/Repos/EntityRepository.cs"] = "public class EntityRepository { public void Add(CreateEntityInput input) { _ = input.ExtraField; } }",
            ["src/Services/EntityService.cs"] = "public class EntityService { public void Save(CreateEntityInput input) { _ = input.ExtraField; } }"
        };

        var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
            evidence,
            sources.Keys,
            touchedFileContents: sources);

        selection.FilePath.Should().Be("src/Models/Entity.cs");
        selection.Reason.Should().Be("UniqueContractOwner");
    }
}
