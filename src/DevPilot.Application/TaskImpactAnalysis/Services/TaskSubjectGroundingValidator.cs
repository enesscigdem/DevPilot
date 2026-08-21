using System.Text.RegularExpressions;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Domain.ProjectBrain;

namespace DevPilot.Application.TaskImpactAnalysis.Services;

public sealed class TaskSubjectGroundingResult
{
    public bool IsGrounded { get; set; } = true;

    public string? TargetEntity { get; set; }

    public string? TargetProperty { get; set; }

    public string? TargetSubject { get; set; }

    public string? UnresolvedReason { get; set; }

    public bool IsEntityMissing { get; set; }

    public bool IsPropertyMissing { get; set; }

    public static TaskSubjectGroundingResult Success(string? entity = null, string? property = null) =>
        new()
        {
            IsGrounded = true,
            TargetEntity = entity,
            TargetProperty = property,
            TargetSubject = !string.IsNullOrWhiteSpace(entity) && !string.IsNullOrWhiteSpace(property)
                ? $"{entity}.{property}"
                : entity ?? property
        };

    public static TaskSubjectGroundingResult Unresolved(
        string subject,
        string reason,
        string? entity = null,
        string? property = null,
        bool isEntityMissing = false,
        bool isPropertyMissing = false) =>
        new()
        {
            IsGrounded = false,
            TargetSubject = subject,
            TargetEntity = entity,
            TargetProperty = property,
            UnresolvedReason = reason,
            IsEntityMissing = isEntityMissing,
            IsPropertyMissing = isPropertyMissing
        };
}

public static class TaskSubjectGroundingValidator
{
    public static TaskSubjectGroundingResult Validate(
        string? taskPrompt,
        RepositoryEvidenceProfile evidence,
        string? workspaceLocalPath)
    {
        if (string.IsNullOrWhiteSpace(taskPrompt))
        {
            return TaskSubjectGroundingResult.Success();
        }

        var (entity, property, isModifyingExisting) = ExtractExplicitSubject(taskPrompt);

        if (string.IsNullOrWhiteSpace(entity))
        {
            // No explicit central entity subject identified in prompt
            return TaskSubjectGroundingResult.Success();
        }

        // 1. Check if the named entity exists in repository evidence
        var entityFound = IsEntityInRepository(entity, evidence, workspaceLocalPath, out var entityFilePath);

        if (!entityFound)
        {
            var subjectName = !string.IsNullOrWhiteSpace(property) ? $"{entity}.{property}" : entity;
            var reason = $"{subjectName} could not be resolved in repository evidence.";
            return TaskSubjectGroundingResult.Unresolved(
                subject: subjectName,
                reason: reason,
                entity: entity,
                property: property,
                isEntityMissing: true);
        }

        // 2. If entity exists, check if an explicitly modified existing property exists
        if (isModifyingExisting && !string.IsNullOrWhiteSpace(property) && !string.IsNullOrWhiteSpace(entityFilePath))
        {
            var propFound = IsPropertyInEntityFile(entityFilePath, property, workspaceLocalPath);
            if (!propFound)
            {
                var subjectName = $"{entity}.{property}";
                var reason = $"{subjectName} could not be resolved in repository evidence.";
                return TaskSubjectGroundingResult.Unresolved(
                    subject: subjectName,
                    reason: reason,
                    entity: entity,
                    property: property,
                    isPropertyMissing: true);
            }
        }

        return TaskSubjectGroundingResult.Success(entity, property);
    }

    private static (string? Entity, string? Property, bool IsModifyingExisting) ExtractExplicitSubject(string prompt)
    {
        string? entity = null;
        string? property = null;
        var isModifyingExisting = false;

        var p = prompt.Trim();

        // Pattern 1: Explicit dot notation (e.g. Customer.Email or Order.DiscountAmount)
        var dotMatch = Regex.Match(p, @"\b([A-Z][a-zA-Z0-9_]+)\.([A-Z][a-zA-Z0-9_]+)\b");
        if (dotMatch.Success)
        {
            entity = dotMatch.Groups[1].Value;
            property = dotMatch.Groups[2].Value;
            isModifyingExisting = IsModifyingIntent(p);
            return (entity, property, isModifyingExisting);
        }

        // Pattern 2: Turkish entity + property syntax
        // Example: "Customer entity’sindeki Email alanını zorunlu hale getirelim..."
        // Example: "Customer entity'sindeki Email alanını..."
        // Example: "Customer'daki Email alanını..."
        // Example: "Customer tablosundaki Email alanını..."
        var trEntityPropMatch = Regex.Match(
            p,
            @"\b([A-Z][a-zA-Z0-9_]+)\s*(?:entity['’]sindeki|entity['’]si|entity['’]deki|entity|modeli|varlığı|tablosu|sınıfı|['’]deki|['’]daki|['’]teki|['’]taki)\s+([A-Z][a-zA-Z0-9_]+)\s*(?:alanı|alanını|property|özelliği|kolonu|sütunu)?",
            RegexOptions.IgnoreCase);

        if (trEntityPropMatch.Success)
        {
            entity = trEntityPropMatch.Groups[1].Value;
            property = trEntityPropMatch.Groups[2].Value;
            isModifyingExisting = IsModifyingIntent(p);
            return (entity, property, isModifyingExisting);
        }

        // Pattern 3: English "Make/Update Customer.Email" or "Make Customer Email required"
        var enMakeMatch = Regex.Match(
            p,
            @"(?:make|update|modify|change|reduce)\s+(?:the\s+)?([A-Z][a-zA-Z0-9_]+)(?:'s|\s+entity's|\s+entity)?\s+([A-Z][a-zA-Z0-9_]+)\s*(?:field|property|column)?",
            RegexOptions.IgnoreCase);

        if (enMakeMatch.Success)
        {
            entity = enMakeMatch.Groups[1].Value;
            property = enMakeMatch.Groups[2].Value;
            isModifyingExisting = true;
            return (entity, property, isModifyingExisting);
        }

        // Pattern 4: "Add [optional/required] Property to Entity" (e.g. "Add optional DiscountAmount to Order entity")
        var enAddMatch = Regex.Match(
            p,
            @"add\s+(?:an? )?(?:optional |nullable |required |non-nullable )?([A-Z][a-zA-Z0-9_]+)(?: field| property| column)?\s+to\s+(?:the\s+)?([A-Z][a-zA-Z0-9_]+)(?:\s+entity|\s+table|\s+model)?",
            RegexOptions.IgnoreCase);

        if (enAddMatch.Success)
        {
            property = enAddMatch.Groups[1].Value;
            entity = enAddMatch.Groups[2].Value;
            isModifyingExisting = false; // Adding new property
            return (entity, property, isModifyingExisting);
        }

        // Pattern 5: Entity only mentions (e.g. "Customer entity", "Customer model", "CustomerService", "CustomerController")
        var entityOnlyMatch = Regex.Match(
            p,
            @"\b([A-Z][a-zA-Z0-9_]+)\s*(?:entity|model|class|tablo|tablosu|varlık|varlığı|sınıf|sınıfı)\b",
            RegexOptions.IgnoreCase);

        if (entityOnlyMatch.Success)
        {
            entity = entityOnlyMatch.Groups[1].Value;
            // Check if property is also mentioned
            var propMatch = Regex.Match(p, @"\b([A-Z][a-zA-Z0-9_]+)\s*(?:field|property|alanı|alanını|column|sütunu)\b", RegexOptions.IgnoreCase);
            if (propMatch.Success && !string.Equals(propMatch.Groups[1].Value, entity, StringComparison.OrdinalIgnoreCase))
            {
                property = propMatch.Groups[1].Value;
            }
            isModifyingExisting = IsModifyingIntent(p);
            return (entity, property, isModifyingExisting);
        }

        return (null, null, false);
    }

    private static bool IsModifyingIntent(string prompt)
    {
        var p = prompt.ToLowerInvariant();
        return p.Contains("zorunlu") ||
               p.Contains("düşür") ||
               p.Contains("artır") ||
               p.Contains("değiştir") ||
               p.Contains("güncelle") ||
               p.Contains("required") ||
               p.Contains("reduce") ||
               p.Contains("modify") ||
               p.Contains("update") ||
               p.Contains("alter") ||
               p.Contains("rename");
    }

    private static bool IsEntityInRepository(
        string entityName,
        RepositoryEvidenceProfile evidence,
        string? workspaceLocalPath,
        out string? matchedFilePath)
    {
        matchedFilePath = null;

        // 1. Check inventory files
        foreach (var file in evidence.InventoryCsFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(fileName, entityName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, $"{entityName}Entity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, $"{entityName}Model", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, $"{entityName}Service", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, $"{entityName}Controller", StringComparison.OrdinalIgnoreCase))
            {
                matchedFilePath = file;
                return true;
            }
        }

        // 2. Check persistence files
        foreach (var file in evidence.PersistenceFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(fileName, entityName, StringComparison.OrdinalIgnoreCase))
            {
                matchedFilePath = file;
                return true;
            }
        }

        // 3. Check controller files
        foreach (var file in evidence.ControllerFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(fileName, $"{entityName}Controller", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, entityName, StringComparison.OrdinalIgnoreCase))
            {
                matchedFilePath = file;
                return true;
            }
        }

        // 4. If local workspace is available, check file contents for class/record/interface declaration
        if (!string.IsNullOrWhiteSpace(workspaceLocalPath) && Directory.Exists(workspaceLocalPath))
        {
            try
            {
                var files = Directory.GetFiles(workspaceLocalPath, "*.cs", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                        file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (string.Equals(fileName, entityName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedFilePath = file;
                        return true;
                    }

                    // Check content for class definition
                    var content = File.ReadAllText(file);
                    if (Regex.IsMatch(content, $@"\b(?:class|record|struct|interface|enum)\s+{Regex.Escape(entityName)}\b"))
                    {
                        matchedFilePath = file;
                        return true;
                    }
                }
            }
            catch
            {
                // Fall back to evidence
            }
        }

        return false;
    }

    private static bool IsPropertyInEntityFile(
        string entityFilePath,
        string propertyName,
        string? workspaceLocalPath)
    {
        string? content = null;

        if (!string.IsNullOrWhiteSpace(workspaceLocalPath) && Directory.Exists(workspaceLocalPath))
        {
            var fullPath = Path.IsPathRooted(entityFilePath)
                ? entityFilePath
                : Path.Combine(workspaceLocalPath, entityFilePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(fullPath))
            {
                try { content = File.ReadAllText(fullPath); } catch { /* Ignore */ }
            }
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            // If file cannot be read directly, assume grounded to avoid false positives on unreadable files
            return true;
        }

        // Check if property is defined
        return Regex.IsMatch(
            content,
            $@"\b{Regex.Escape(propertyName)}\b\s*\{{\s*(?:get|set|init)",
            RegexOptions.IgnoreCase) ||
            Regex.IsMatch(
                content,
                $@"public\s+[\w<>?,\[\]\s]+\s+{Regex.Escape(propertyName)}\b",
                RegexOptions.IgnoreCase);
    }
}
