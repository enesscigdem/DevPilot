using System.Text.RegularExpressions;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.TaskImpactAnalysis.Services;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;

namespace DevPilot.Infrastructure.DatabaseIntelligence;

public sealed class EfCoreDatabaseImpactAnalyzer : IDatabaseImpactAnalyzer
{
    public DatabaseImpact AnalyzeImpact(
        IReadOnlyList<ImpactedFile> impactedFiles,
        IReadOnlyList<ChangeDimensionImpact> dimensions,
        IReadOnlyList<Risk> risks,
        RepositoryEvidenceProfile evidence,
        string? taskPrompt = null,
        string? workspaceRoot = null)
    {
        var dataDim = dimensions.FirstOrDefault(d => string.Equals(d.Area, ChangeDimensionArea.Data, StringComparison.OrdinalIgnoreCase));

        var hasPersistenceImpact = impactedFiles.Any(f =>
            f.EvidenceType is "PersistenceRelationship" or "MigrationRelationship" ||
            f.FilePath.Contains("DbContext", StringComparison.OrdinalIgnoreCase) ||
            f.FilePath.Contains("/Entities/", StringComparison.OrdinalIgnoreCase) ||
            f.FilePath.Contains("/Models/", StringComparison.OrdinalIgnoreCase) ||
            f.FilePath.Contains("Configuration.cs", StringComparison.OrdinalIgnoreCase) ||
            f.FilePath.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase));

        var promptHasDbIntent = HasDatabaseIntentInPrompt(taskPrompt);

        // If repository has no EF Core and no persistence files impacted and prompt has no DB intent, return non-database impact
        if (!evidence.HasEfCore && !hasPersistenceImpact && dataDim == null && !promptHasDbIntent)
        {
            return new DatabaseImpact
            {
                RequiresSchemaMigration = false,
                MigrationRequirement = DatabaseMigrationRequirement.None,
                MigrationConfidence = 0,
                ChangeKind = DatabaseChangeKind.None,
                DataRiskLevel = RiskLevel.Low,
                RequiresDataMigration = false,
                DataMigrationRequirement = DataMigrationRequirement.None,
                Summary = "No database or schema changes detected",
                Changes = new List<DatabaseChange>(),
                Evidence = new List<string>(),
                Unknowns = new List<string>()
            };
        }

        var changes = new List<DatabaseChange>();
        var unknowns = new List<string>();
        var evidenceList = new List<string>();

        if (evidence.HasEfCore)
        {
            evidenceList.Add("EF Core package references detected in repository");
        }

        if (evidence.MigrationFiles.Count > 0)
        {
            evidenceList.Add($"{evidence.MigrationFiles.Count} historical migration/snapshot file(s) found in repository (historical evidence only; never modified for new schema changes)");
        }

        // Analyze task prompt and files for specific database operations
        AnalyzeTaskAndFiles(taskPrompt, impactedFiles, workspaceRoot, changes, unknowns, evidenceList);

        // If no specific changes were extracted yet, but persistence files or data dimension exists
        if (changes.Count == 0 && (hasPersistenceImpact || dataDim != null || promptHasDbIntent))
        {
            changes.Add(new DatabaseChange
            {
                ObjectType = DatabaseObjectType.Unknown,
                ObjectName = "Schema / Persistence Entity",
                Operation = DatabaseChangeOperation.Alter,
                Risk = dataDim?.ImpactLevel >= SystemImpactLevel.High ? RiskLevel.High : RiskLevel.Medium,
                Evidence = "Persistence entities or data mappings modified in task scope"
            });
        }

        // Evaluate overall risk and change kind
        var hasDestructive = changes.Any(c => c.Risk >= RiskLevel.High ||
                                              c.Operation == DatabaseChangeOperation.Remove ||
                                              c.Evidence.Contains("rename", StringComparison.OrdinalIgnoreCase) ||
                                              c.Evidence.Contains("reduction", StringComparison.OrdinalIgnoreCase) ||
                                              c.Evidence.Contains("required without", StringComparison.OrdinalIgnoreCase));

        var hasPotentiallyDataSensitive = changes.Any(c => c.Risk == RiskLevel.Medium ||
                                                           c.ObjectType == DatabaseObjectType.Index ||
                                                           c.ObjectType == DatabaseObjectType.Relationship ||
                                                           c.Evidence.Contains("nullable -> required", StringComparison.OrdinalIgnoreCase) ||
                                                           c.Evidence.Contains("becomes required", StringComparison.OrdinalIgnoreCase));

        DatabaseChangeKind changeKind;
        RiskLevel dataRiskLevel;

        if (hasDestructive)
        {
            changeKind = DatabaseChangeKind.Destructive;
            dataRiskLevel = RiskLevel.High;
        }
        else if (hasPotentiallyDataSensitive)
        {
            changeKind = DatabaseChangeKind.PotentiallyDataSensitive;
            dataRiskLevel = RiskLevel.Medium;
        }
        else if (changes.Count > 0)
        {
            changeKind = DatabaseChangeKind.Additive;
            dataRiskLevel = RiskLevel.Low;
        }
        else
        {
            changeKind = DatabaseChangeKind.None;
            dataRiskLevel = RiskLevel.Low;
        }

        // Check if data migration / backfill is required
        var requiresDataMigration = false;
        var dataMigrationRequirement = DataMigrationRequirement.None;

        var requiredAdditionsWithoutDefault = changes.Where(c =>
            c.Evidence.Contains("becomes required", StringComparison.OrdinalIgnoreCase) ||
            c.Evidence.Contains("required without", StringComparison.OrdinalIgnoreCase) ||
            c.Evidence.Contains("nullable -> required", StringComparison.OrdinalIgnoreCase) ||
            c.Evidence.Contains("reduction", StringComparison.OrdinalIgnoreCase) ||
            c.Evidence.Contains("backfill", StringComparison.OrdinalIgnoreCase)).ToList();

        if (requiredAdditionsWithoutDefault.Count > 0)
        {
            requiresDataMigration = true;
            dataMigrationRequirement = DataMigrationRequirement.ReviewRequired;
            dataRiskLevel = RiskLevel.High; // Explicit rule: Non-nullable additions / nullable->required without deterministic default/backfill are HIGH risk
            changeKind = DatabaseChangeKind.PotentiallyDataSensitive;
        }

        var requiresSchemaMigration = changes.Count > 0 || hasPersistenceImpact || promptHasDbIntent;
        var migrationRequirement = requiresSchemaMigration
            ? DatabaseMigrationRequirement.Expected
            : DatabaseMigrationRequirement.None;

        var confidence = requiresSchemaMigration ? 90 : 20;

        // Build deterministic summary
        var summary = BuildSummary(changes, changeKind, dataRiskLevel, requiresDataMigration);

        // Unknowns: exact generated migration operations are not known until implementation
        if (requiresSchemaMigration)
        {
            unknowns.Add("Exact generated migration operations are not known until implementation");
        }

        return new DatabaseImpact
        {
            RequiresSchemaMigration = requiresSchemaMigration,
            MigrationRequirement = migrationRequirement,
            MigrationConfidence = confidence,
            ChangeKind = changeKind,
            DataRiskLevel = dataRiskLevel,
            RequiresDataMigration = requiresDataMigration,
            DataMigrationRequirement = dataMigrationRequirement,
            Summary = summary,
            Changes = changes,
            Evidence = evidenceList.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Unknowns = unknowns.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static bool HasDatabaseIntentInPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var p = prompt.ToLowerInvariant();
        return p.Contains("migration") ||
               p.Contains("database") ||
               p.Contains("dbcontext") ||
               p.Contains("entity") ||
               p.Contains("table") ||
               p.Contains("column") ||
               p.Contains("field") ||
               p.Contains("schema") ||
               p.Contains("discountamount") ||
               p.Contains("email") ||
               p.Contains("foreign key") ||
               p.Contains("index");
    }

    private static void AnalyzeTaskAndFiles(
        string? prompt,
        IReadOnlyList<ImpactedFile> files,
        string? workspaceRoot,
        List<DatabaseChange> changes,
        List<string> unknowns,
        List<string> evidenceList)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;

        var p = prompt.ToLowerInvariant();

        // Pattern 1: Add required / non-nullable field
        // Example: "Add required Email field to Customer" / "Add non-nullable Status to Order"
        var addReqMatch = Regex.Match(prompt, @"add (?:a |an )?required (?:non-nullable )?(\w+)(?: field| property| column)?(?: to (?:entity )?(\w+))?", RegexOptions.IgnoreCase);
        if (addReqMatch.Success)
        {
            var propName = addReqMatch.Groups[1].Value;
            var targetEntity = addReqMatch.Groups[2].Success ? addReqMatch.Groups[2].Value : (DetectTargetEntity(prompt, files) ?? "Entity");
            changes.Add(new DatabaseChange
            {
                ObjectType = DatabaseObjectType.Column,
                ObjectName = propName,
                ParentObjectName = targetEntity,
                Operation = DatabaseChangeOperation.Add,
                Risk = RiskLevel.High,
                Evidence = $"{targetEntity}.{propName} is a new required column without proven default/backfill"
            });
            evidenceList.Add($"Detected non-nullable required property addition '{propName}' on '{targetEntity}' in task prompt");
            unknowns.Add($"Adding non-nullable field '{propName}' on '{targetEntity}' without deterministic default value requires backfill or default value review");
        }

        // Pattern 2: Add optional / nullable field
        // Example: "Add an optional DiscountAmount field" / "Add nullable CouponId" / "Add optional DiscountAmount to Order entity"
        var optMatch = Regex.Match(prompt, @"add (?:an? )?optional (?:nullable )?(\w+)(?: field| property| column)?(?: to (?:entity )?(\w+))?", RegexOptions.IgnoreCase);
        if (optMatch.Success && !addReqMatch.Success)
        {
            var propName = optMatch.Groups[1].Value;
            var targetEntity = optMatch.Groups[2].Success ? optMatch.Groups[2].Value : (DetectTargetEntity(prompt, files) ?? "Entity");
            changes.Add(new DatabaseChange
            {
                ObjectType = DatabaseObjectType.Column,
                ObjectName = propName,
                ParentObjectName = targetEntity,
                Operation = DatabaseChangeOperation.Add,
                Risk = RiskLevel.Low,
                Evidence = $"Additive nullable column '{propName}' on {targetEntity}"
            });
            evidenceList.Add($"Detected optional property addition '{propName}' on '{targetEntity}' in task prompt");
        }

        // Pattern 3: Generic property addition
        // Example: "Add SKU property to Product"
        var genAddMatch = Regex.Match(prompt, @"add (?:a |an )?(?:new )?(?:property |column |field )?(\w+)(?: property| column| field)? to (?:entity )?(\w+)", RegexOptions.IgnoreCase);
        if (genAddMatch.Success && !addReqMatch.Success && !optMatch.Success)
        {
            var propName = genAddMatch.Groups[1].Value;
            var targetEntity = genAddMatch.Groups[2].Value;
            var isRequired = p.Contains("required") || p.Contains("non-nullable");
            changes.Add(new DatabaseChange
            {
                ObjectType = DatabaseObjectType.Column,
                ObjectName = propName,
                ParentObjectName = targetEntity,
                Operation = DatabaseChangeOperation.Add,
                Risk = isRequired ? RiskLevel.High : RiskLevel.Low,
                Evidence = isRequired
                    ? $"{targetEntity}.{propName} is a new required column without proven default/backfill"
                    : $"Additive column '{propName}' on {targetEntity}"
            });
            evidenceList.Add($"Detected property addition '{propName}' on '{targetEntity}' in task prompt");
        }

        // Pattern 4: Make property required / non-nullable (English & Turkish)
        // Example: "Make Customer.Email required" or "Customer entity’sindeki Email alanını zorunlu hale getirelim"
        var reqMatch = Regex.Match(prompt, @"make (?:(\w+)[.\s])?(\w+) (?:required|non-nullable|not null)", RegexOptions.IgnoreCase);
        var trReqMatch = Regex.Match(prompt, @"(?:(\w+)\s*(?:entity['’]sindeki|entity['’]si|varlığı|tablosu)?\s+)?(\w+)\s+(?:alanını|alanı|property)?\s*(?:zorunlu|zorunlu hale)", RegexOptions.IgnoreCase);

        if ((reqMatch.Success || trReqMatch.Success) && !addReqMatch.Success)
        {
            var match = reqMatch.Success ? reqMatch : trReqMatch;
            var entityName = !string.IsNullOrWhiteSpace(match.Groups[1].Value) ? match.Groups[1].Value : (DetectTargetEntity(prompt, files) ?? "Customer");
            var propName = match.Groups[2].Value;

            changes.Add(new DatabaseChange
            {
                ObjectType = DatabaseObjectType.Column,
                ObjectName = propName,
                ParentObjectName = entityName,
                Operation = DatabaseChangeOperation.Alter,
                Risk = RiskLevel.High,
                Evidence = $"{entityName}.{propName} becomes required without proven default/backfill"
            });
            evidenceList.Add($"Detected requirement transition to non-nullable for '{entityName}.{propName}'");
            unknowns.Add($"Existing rows in {entityName} may have null values for {propName}; review default/backfill strategy");
        }

        // Pattern 5: Reduce max length (English & Turkish)
        // Example: "reduce max length from 500 to 200" or "maksimum uzunluğunu 500’den 200’e düşürelim"
        var lenMatch = Regex.Match(prompt, @"reduce (?:max )?length (?:of (?:(\w+)[.\s])?(\w+) )?from (\d+) to (\d+)", RegexOptions.IgnoreCase);
        var trLenMatch = Regex.Match(prompt, @"(?:maksimum\s+)?uzunlu[ğg]unu\s+(\d+)[’']?d[ea]n\s+(\d+)[’']?[yea]?\s*(?:düşür|indir)", RegexOptions.IgnoreCase);

        if (lenMatch.Success || trLenMatch.Success)
        {
            int fromLen, toLen;
            string? entityName = null;
            string? propName = null;

            if (lenMatch.Success)
            {
                fromLen = int.Parse(lenMatch.Groups[3].Value);
                toLen = int.Parse(lenMatch.Groups[4].Value);
                entityName = !string.IsNullOrWhiteSpace(lenMatch.Groups[1].Value) ? lenMatch.Groups[1].Value : null;
                propName = !string.IsNullOrWhiteSpace(lenMatch.Groups[2].Value) ? lenMatch.Groups[2].Value : null;
            }
            else
            {
                fromLen = int.Parse(trLenMatch.Groups[1].Value);
                toLen = int.Parse(trLenMatch.Groups[2].Value);
            }

            entityName ??= DetectTargetEntity(prompt, files) ?? "Entity";
            propName ??= changes.FirstOrDefault(c => c.ObjectType == DatabaseObjectType.Column)?.ObjectName ?? "Property";

            evidenceList.Add($"Detected max length reduction ({fromLen} -> {toLen}) for '{entityName}.{propName}'");

            // If we already have a change for this property, enrich it; otherwise add
            var existing = changes.FirstOrDefault(c => string.Equals(c.ObjectName, propName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Risk = RiskLevel.High;
                existing.Before = $"maxLength: {fromLen}";
                existing.After = $"maxLength: {toLen}";
                existing.Evidence += $" and reduced max length from {fromLen} to {toLen}";
            }
            else
            {
                changes.Add(new DatabaseChange
                {
                    ObjectType = DatabaseObjectType.Column,
                    ObjectName = propName,
                    ParentObjectName = entityName,
                    Operation = DatabaseChangeOperation.Alter,
                    Before = $"maxLength: {fromLen}",
                    After = $"maxLength: {toLen}",
                    Risk = RiskLevel.High,
                    Evidence = $"Max length reduced from {fromLen} to {toLen} on {entityName}.{propName} (potential data truncation risk)"
                });
            }
            unknowns.Add($"Existing rows with length > {toLen} in {entityName}.{propName} may be truncated or cause migration failures");
        }

        // Pattern 4: Precision / scale reduction
        var precMatch = Regex.Match(prompt, @"reduce (?:decimal )?precision (?:from (\d+) to (\d+)|of (?:(\w+)\.)?(\w+))", RegexOptions.IgnoreCase);
        if (precMatch.Success)
        {
            var entityName = DetectTargetEntity(prompt, files) ?? "Entity";
            var propName = precMatch.Groups[4].Success ? precMatch.Groups[4].Value : "Amount";
            changes.Add(new DatabaseChange
            {
                ObjectType = DatabaseObjectType.Column,
                ObjectName = propName,
                ParentObjectName = entityName,
                Operation = DatabaseChangeOperation.Alter,
                Risk = RiskLevel.High,
                Evidence = $"Decimal precision/scale reduced on {entityName}.{propName} (data loss / truncation risk)"
            });
        }

        // Pattern 5: Drop column / Drop table
        if (p.Contains("drop column") || p.Contains("remove column") || p.Contains("delete column"))
        {
            var dropColMatch = Regex.Match(prompt, @"(?:drop|remove|delete) column (?:(\w+)\.)?(\w+)", RegexOptions.IgnoreCase);
            var colName = dropColMatch.Success ? dropColMatch.Groups[2].Value : "Column";
            var table = dropColMatch.Success && dropColMatch.Groups[1].Success ? dropColMatch.Groups[1].Value : (DetectTargetEntity(prompt, files) ?? "Table");
            changes.Add(new DatabaseChange
            {
                ObjectType = DatabaseObjectType.Column,
                ObjectName = colName,
                ParentObjectName = table,
                Operation = DatabaseChangeOperation.Remove,
                Risk = RiskLevel.High,
                Evidence = $"Destructive column removal '{colName}' on {table}"
            });
        }

        if (p.Contains("drop table") || p.Contains("remove table") || p.Contains("delete table"))
        {
            var dropTableMatch = Regex.Match(prompt, @"(?:drop|remove|delete) table (\w+)", RegexOptions.IgnoreCase);
            var tableName = dropTableMatch.Success ? dropTableMatch.Groups[1].Value : "Table";
            changes.Add(new DatabaseChange
            {
                ObjectType = DatabaseObjectType.Table,
                ObjectName = tableName,
                Operation = DatabaseChangeOperation.Remove,
                Risk = RiskLevel.High,
                Evidence = $"Destructive table removal '{tableName}'"
            });
        }

        // Pattern 6: New table / entity
        var newTableMatch = Regex.Match(prompt, @"(?:add|create) (?:a )?(?:new )?(?:persisted )?(?:table|entity) (\w+)", RegexOptions.IgnoreCase);
        if (newTableMatch.Success)
        {
            var tableName = newTableMatch.Groups[1].Value;
            changes.Add(new DatabaseChange
            {
                ObjectType = DatabaseObjectType.Table,
                ObjectName = tableName,
                Operation = DatabaseChangeOperation.Add,
                Risk = RiskLevel.Low,
                Evidence = $"New table/entity '{tableName}'"
            });
        }

        // Pattern 7: Unique index
        if (p.Contains("unique index") || p.Contains("unique constraint"))
        {
            var targetEntity = DetectTargetEntity(prompt, files) ?? "Entity";
            changes.Add(new DatabaseChange
            {
                ObjectType = DatabaseObjectType.Index,
                ObjectName = $"IX_{targetEntity}_Unique",
                ParentObjectName = targetEntity,
                Operation = DatabaseChangeOperation.Add,
                Risk = RiskLevel.Medium,
                Evidence = $"Unique index addition on {targetEntity} (may fail if duplicate data exists in database)"
            });
            unknowns.Add($"Existing duplicate values on {targetEntity} will cause unique index creation to fail");
        }

        // Pattern 8: New required foreign key
        if (p.Contains("required foreign key") || p.Contains("required fk") || p.Contains("required relationship"))
        {
            var targetEntity = DetectTargetEntity(prompt, files) ?? "Entity";
            changes.Add(new DatabaseChange
            {
                ObjectType = DatabaseObjectType.Relationship,
                ObjectName = $"FK_{targetEntity}",
                ParentObjectName = targetEntity,
                Operation = DatabaseChangeOperation.Add,
                Risk = RiskLevel.High,
                Evidence = $"New required foreign key on {targetEntity} without default/backfill"
            });
            unknowns.Add($"Existing rows in {targetEntity} without matching parent rows will violate foreign key constraint");
        }

        // Pattern 9: Ambiguous rename vs drop+add
        if ((p.Contains("rename") || p.Contains("renaming")) && !p.Contains("migrationbuilder.rename"))
        {
            var targetEntity = DetectTargetEntity(prompt, files) ?? "Entity";
            changes.Add(new DatabaseChange
            {
                ObjectType = DatabaseObjectType.Column,
                ObjectName = "RenamedProperty",
                ParentObjectName = targetEntity,
                Operation = DatabaseChangeOperation.Unknown,
                Risk = RiskLevel.High,
                Evidence = "Possible rename vs drop+add — data loss risk without explicit MigrationBuilder.RenameColumn in migration"
            });
            unknowns.Add("Inferred rename without deterministic migration evidence may result in drop+add and permanent data loss");
        }
    }

    private static string? DetectTargetEntity(string prompt, IReadOnlyList<ImpactedFile> files)
    {
        // Check files first (e.g. Order.cs -> Order)
        foreach (var f in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(f.FilePath);
            if (!string.IsNullOrWhiteSpace(fileName) && !fileName.Contains("Test", StringComparison.OrdinalIgnoreCase) && !fileName.Contains("Controller", StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }
        }

        // Heuristics from prompt
        if (prompt.Contains("Order", StringComparison.OrdinalIgnoreCase)) return "Order";
        if (prompt.Contains("Customer", StringComparison.OrdinalIgnoreCase)) return "Customer";
        if (prompt.Contains("User", StringComparison.OrdinalIgnoreCase)) return "User";
        if (prompt.Contains("Todo", StringComparison.OrdinalIgnoreCase)) return "Todo";
        if (prompt.Contains("Coupon", StringComparison.OrdinalIgnoreCase)) return "Coupon";

        return null;
    }

    private static string BuildSummary(
        List<DatabaseChange> changes,
        DatabaseChangeKind kind,
        RiskLevel riskLevel,
        bool requiresDataMigration)
    {
        if (changes.Count == 0)
        {
            return "No database schema changes expected";
        }

        var changeDescriptions = string.Join("; ", changes.Select(c => c.Evidence));

        if (requiresDataMigration)
        {
            return $"{changeDescriptions}. A schema migration is expected and a backfill/default strategy should be reviewed.";
        }

        if (riskLevel >= RiskLevel.High)
        {
            return $"High risk database modifications: {changeDescriptions}. Schema migration required with careful operational review.";
        }

        return $"Database schema changes detected ({kind}): {changeDescriptions}.";
    }
}
