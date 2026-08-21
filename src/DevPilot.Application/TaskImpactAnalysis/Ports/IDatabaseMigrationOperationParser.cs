using DevPilot.Domain.ValueObjects;

namespace DevPilot.Application.TaskImpactAnalysis.Ports;

public interface IDatabaseMigrationOperationParser
{
    IReadOnlyList<DatabaseChange> ParseMigrationFile(string filePath, string fileContent);
}
