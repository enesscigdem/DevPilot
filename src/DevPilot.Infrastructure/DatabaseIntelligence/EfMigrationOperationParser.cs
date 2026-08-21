using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DevPilot.Infrastructure.DatabaseIntelligence;

public sealed class EfMigrationOperationParser : IDatabaseMigrationOperationParser
{
    public IReadOnlyList<DatabaseChange> ParseMigrationFile(string filePath, string fileContent)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
        {
            return Array.Empty<DatabaseChange>();
        }

        // Designer files only contain metadata, not Up() / Down() migration operations
        if (filePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<DatabaseChange>();
        }

        var syntaxTree = CSharpSyntaxTree.ParseText(fileContent);
        var root = syntaxTree.GetRoot();

        // Find Up() method strictly - Down() is rollback evidence only
        var upMethod = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => string.Equals(m.Identifier.Text, "Up", StringComparison.Ordinal));

        if (upMethod == null)
        {
            return Array.Empty<DatabaseChange>();
        }

        var changes = new List<DatabaseChange>();

        var invocations = upMethod.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var inv in invocations)
        {
            var methodName = GetMethodName(inv);
            if (string.IsNullOrWhiteSpace(methodName)) continue;

            // Only inspect invocations on migrationBuilder or table builder
            var receiverText = GetReceiverName(inv);
            var isMigrationBuilder = receiverText.Contains("migrationBuilder", StringComparison.OrdinalIgnoreCase);

            if (isMigrationBuilder)
            {
                switch (methodName)
                {
                    case "CreateTable":
                        ParseCreateTable(inv, changes);
                        break;

                    case "DropTable":
                        ParseDropTable(inv, changes);
                        break;

                    case "AddColumn":
                        ParseAddColumn(inv, changes);
                        break;

                    case "DropColumn":
                        ParseDropColumn(inv, changes);
                        break;

                    case "AlterColumn":
                        ParseAlterColumn(inv, changes);
                        break;

                    case "RenameColumn":
                        ParseRenameColumn(inv, changes);
                        break;

                    case "RenameTable":
                        ParseRenameTable(inv, changes);
                        break;

                    case "CreateIndex":
                        ParseCreateIndex(inv, changes);
                        break;

                    case "DropIndex":
                        ParseDropIndex(inv, changes);
                        break;

                    case "AddForeignKey":
                        ParseAddForeignKey(inv, changes);
                        break;

                    case "DropForeignKey":
                        ParseDropForeignKey(inv, changes);
                        break;

                    case "Sql":
                        var sql = GetArgumentStringValue(inv, "sql", 0) ?? "SQL";
                        changes.Add(new DatabaseChange
                        {
                            ObjectType = DatabaseObjectType.Unknown,
                            ObjectName = "Sql",
                            Operation = DatabaseChangeOperation.Alter,
                            Risk = RiskLevel.High,
                            Evidence = $"Raw SQL migration operation detected — manual review required: {sql}"
                        });
                        break;

                    default:
                        // Unsupported or exotic migration operations
                        changes.Add(new DatabaseChange
                        {
                            ObjectType = DatabaseObjectType.Unknown,
                            ObjectName = methodName,
                            Operation = DatabaseChangeOperation.Unknown,
                            Risk = RiskLevel.Medium,
                            Evidence = $"Unsupported or custom migration operation '{methodName}' — manual review required"
                        });
                        break;
                }
            }
        }

        return changes;
    }

    private static void ParseCreateTable(InvocationExpressionSyntax inv, List<DatabaseChange> changes)
    {
        var name = GetArgumentStringValue(inv, "name", 0) ?? "UnknownTable";
        changes.Add(new DatabaseChange
        {
            ObjectType = DatabaseObjectType.Table,
            ObjectName = name,
            Operation = DatabaseChangeOperation.Add,
            Risk = RiskLevel.Low,
            Evidence = $"CreateTable '{name}' in migration"
        });
    }

    private static void ParseDropTable(InvocationExpressionSyntax inv, List<DatabaseChange> changes)
    {
        var name = GetArgumentStringValue(inv, "name", 0) ?? "UnknownTable";
        changes.Add(new DatabaseChange
        {
            ObjectType = DatabaseObjectType.Table,
            ObjectName = name,
            Operation = DatabaseChangeOperation.Remove,
            Risk = RiskLevel.High,
            Evidence = $"DropTable '{name}' in migration"
        });
    }

    private static void ParseAddColumn(InvocationExpressionSyntax inv, List<DatabaseChange> changes)
    {
        var name = GetArgumentStringValue(inv, "name", 0) ?? "UnknownColumn";
        var table = GetArgumentStringValue(inv, "table", 1);
        var nullable = GetArgumentBoolValue(inv, "nullable");
        var hasDefault = GetArgumentExpression(inv, "defaultValue") != null ||
                         GetArgumentExpression(inv, "defaultValueSql") != null;

        RiskLevel risk;
        if (nullable == true)
        {
            risk = RiskLevel.Low;
        }
        else if (nullable == false && !hasDefault)
        {
            // Adding non-nullable column without default to existing table is High risk
            risk = RiskLevel.High;
        }
        else if (nullable == false && hasDefault)
        {
            risk = RiskLevel.Medium;
        }
        else
        {
            risk = RiskLevel.Low;
        }

        changes.Add(new DatabaseChange
        {
            ObjectType = DatabaseObjectType.Column,
            ObjectName = name,
            ParentObjectName = table,
            Operation = DatabaseChangeOperation.Add,
            Risk = risk,
            Evidence = $"AddColumn '{name}' to table '{table ?? "Unknown"}'"
        });
    }

    private static void ParseDropColumn(InvocationExpressionSyntax inv, List<DatabaseChange> changes)
    {
        var name = GetArgumentStringValue(inv, "name", 0) ?? "UnknownColumn";
        var table = GetArgumentStringValue(inv, "table", 1);

        changes.Add(new DatabaseChange
        {
            ObjectType = DatabaseObjectType.Column,
            ObjectName = name,
            ParentObjectName = table,
            Operation = DatabaseChangeOperation.Remove,
            Risk = RiskLevel.High,
            Evidence = $"DropColumn '{name}' from table '{table ?? "Unknown"}'"
        });
    }

    private static void ParseAlterColumn(InvocationExpressionSyntax inv, List<DatabaseChange> changes)
    {
        var name = GetArgumentStringValue(inv, "name", 0) ?? "UnknownColumn";
        var table = GetArgumentStringValue(inv, "table", 1);
        var nullable = GetArgumentBoolValue(inv, "nullable");
        var oldNullable = GetArgumentBoolValue(inv, "oldNullable");
        var maxLength = GetArgumentIntValue(inv, "maxLength");
        var oldMaxLength = GetArgumentIntValue(inv, "oldMaxLength");
        var precision = GetArgumentIntValue(inv, "precision");
        var oldPrecision = GetArgumentIntValue(inv, "oldPrecision");
        var scale = GetArgumentIntValue(inv, "scale");
        var oldScale = GetArgumentIntValue(inv, "oldScale");

        var isRiskElevated = false;
        var reasons = new List<string>();

        if (oldNullable == true && nullable == false)
        {
            isRiskElevated = true;
            reasons.Add("nullable -> required");
        }

        if (oldMaxLength.HasValue && maxLength.HasValue && maxLength.Value < oldMaxLength.Value)
        {
            isRiskElevated = true;
            reasons.Add($"max length reduced from {oldMaxLength.Value} to {maxLength.Value}");
        }

        if (oldPrecision.HasValue && precision.HasValue && precision.Value < oldPrecision.Value)
        {
            isRiskElevated = true;
            reasons.Add($"precision reduced from {oldPrecision.Value} to {precision.Value}");
        }

        if (oldScale.HasValue && scale.HasValue && scale.Value < oldScale.Value)
        {
            isRiskElevated = true;
            reasons.Add($"scale reduced from {oldScale.Value} to {scale.Value}");
        }

        var risk = isRiskElevated ? RiskLevel.High : RiskLevel.Medium;
        var reasonSuffix = reasons.Count > 0 ? $" ({string.Join(", ", reasons)})" : "";

        changes.Add(new DatabaseChange
        {
            ObjectType = DatabaseObjectType.Column,
            ObjectName = name,
            ParentObjectName = table,
            Operation = DatabaseChangeOperation.Alter,
            Before = oldMaxLength.HasValue ? $"maxLength: {oldMaxLength}" : (oldNullable.HasValue ? $"nullable: {oldNullable}" : null),
            After = maxLength.HasValue ? $"maxLength: {maxLength}" : (nullable.HasValue ? $"nullable: {nullable}" : null),
            Risk = risk,
            Evidence = $"AlterColumn '{name}' on table '{table ?? "Unknown"}'{reasonSuffix}"
        });
    }

    private static void ParseRenameColumn(InvocationExpressionSyntax inv, List<DatabaseChange> changes)
    {
        var name = GetArgumentStringValue(inv, "name", 0) ?? "UnknownColumn";
        var table = GetArgumentStringValue(inv, "table", 1);
        var newName = GetArgumentStringValue(inv, "newName", 2) ?? "UnknownNewName";

        changes.Add(new DatabaseChange
        {
            ObjectType = DatabaseObjectType.Column,
            ObjectName = newName,
            ParentObjectName = table,
            Operation = DatabaseChangeOperation.Rename,
            Before = name,
            After = newName,
            Risk = RiskLevel.High,
            Evidence = $"RenameColumn '{name}' to '{newName}' on table '{table ?? "Unknown"}'"
        });
    }

    private static void ParseRenameTable(InvocationExpressionSyntax inv, List<DatabaseChange> changes)
    {
        var name = GetArgumentStringValue(inv, "name", 0) ?? "UnknownTable";
        var newName = GetArgumentStringValue(inv, "newName", 1) ?? "UnknownNewName";

        changes.Add(new DatabaseChange
        {
            ObjectType = DatabaseObjectType.Table,
            ObjectName = newName,
            Operation = DatabaseChangeOperation.Rename,
            Before = name,
            After = newName,
            Risk = RiskLevel.High,
            Evidence = $"RenameTable '{name}' to '{newName}'"
        });
    }

    private static void ParseCreateIndex(InvocationExpressionSyntax inv, List<DatabaseChange> changes)
    {
        var name = GetArgumentStringValue(inv, "name", 0) ?? "UnknownIndex";
        var table = GetArgumentStringValue(inv, "table", 1);
        var unique = GetArgumentBoolValue(inv, "unique") ?? false;

        changes.Add(new DatabaseChange
        {
            ObjectType = DatabaseObjectType.Index,
            ObjectName = name,
            ParentObjectName = table,
            Operation = DatabaseChangeOperation.Add,
            Risk = unique ? RiskLevel.Medium : RiskLevel.Low,
            Evidence = $"CreateIndex '{name}' on table '{table ?? "Unknown"}'{(unique ? " (Unique)" : "")}"
        });
    }

    private static void ParseDropIndex(InvocationExpressionSyntax inv, List<DatabaseChange> changes)
    {
        var name = GetArgumentStringValue(inv, "name", 0) ?? "UnknownIndex";
        var table = GetArgumentStringValue(inv, "table", 1);

        changes.Add(new DatabaseChange
        {
            ObjectType = DatabaseObjectType.Index,
            ObjectName = name,
            ParentObjectName = table,
            Operation = DatabaseChangeOperation.Remove,
            Risk = RiskLevel.Medium,
            Evidence = $"DropIndex '{name}' on table '{table ?? "Unknown"}'"
        });
    }

    private static void ParseAddForeignKey(InvocationExpressionSyntax inv, List<DatabaseChange> changes)
    {
        var name = GetArgumentStringValue(inv, "name", 0) ?? "UnknownFK";
        var table = GetArgumentStringValue(inv, "table", 1);
        var principalTable = GetArgumentStringValue(inv, "principalTable");

        changes.Add(new DatabaseChange
        {
            ObjectType = DatabaseObjectType.Relationship,
            ObjectName = name,
            ParentObjectName = table,
            Operation = DatabaseChangeOperation.Add,
            Risk = RiskLevel.Medium,
            Evidence = $"AddForeignKey '{name}' on table '{table ?? "Unknown"}' referencing '{principalTable ?? "Unknown"}'"
        });
    }

    private static void ParseDropForeignKey(InvocationExpressionSyntax inv, List<DatabaseChange> changes)
    {
        var name = GetArgumentStringValue(inv, "name", 0) ?? "UnknownFK";
        var table = GetArgumentStringValue(inv, "table", 1);

        changes.Add(new DatabaseChange
        {
            ObjectType = DatabaseObjectType.Relationship,
            ObjectName = name,
            ParentObjectName = table,
            Operation = DatabaseChangeOperation.Remove,
            Risk = RiskLevel.High,
            Evidence = $"DropForeignKey '{name}' from table '{table ?? "Unknown"}'"
        });
    }

    private static string? GetMethodName(InvocationExpressionSyntax inv)
    {
        if (inv.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Name.Identifier.Text;
        }
        return null;
    }

    private static string GetReceiverName(InvocationExpressionSyntax inv)
    {
        if (inv.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Expression.ToString();
        }
        return string.Empty;
    }

    private static ExpressionSyntax? GetArgumentExpression(InvocationExpressionSyntax inv, string parameterName, int positionalIndex = -1)
    {
        var args = inv.ArgumentList.Arguments;
        foreach (var arg in args)
        {
            if (arg.NameColon != null && string.Equals(arg.NameColon.Name.Identifier.Text, parameterName, StringComparison.OrdinalIgnoreCase))
            {
                return arg.Expression;
            }
        }

        if (positionalIndex >= 0 && positionalIndex < args.Count && args[positionalIndex].NameColon == null)
        {
            return args[positionalIndex].Expression;
        }

        return null;
    }

    private static string? GetArgumentStringValue(InvocationExpressionSyntax inv, string parameterName, int positionalIndex = -1)
    {
        var expr = GetArgumentExpression(inv, parameterName, positionalIndex);
        if (expr is LiteralExpressionSyntax lit && lit.Token.Value is string s)
        {
            return s;
        }
        if (expr != null)
        {
            var raw = expr.ToString().Trim('"', '@');
            if (!string.IsNullOrWhiteSpace(raw)) return raw;
        }
        return null;
    }

    private static bool? GetArgumentBoolValue(InvocationExpressionSyntax inv, string parameterName, int positionalIndex = -1)
    {
        var expr = GetArgumentExpression(inv, parameterName, positionalIndex);
        if (expr != null)
        {
            if (expr is LiteralExpressionSyntax lit)
            {
                if (lit.Token.Value is bool b) return b;
            }
            if (bool.TryParse(expr.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static int? GetArgumentIntValue(InvocationExpressionSyntax inv, string parameterName, int positionalIndex = -1)
    {
        var expr = GetArgumentExpression(inv, parameterName, positionalIndex);
        if (expr is LiteralExpressionSyntax lit)
        {
            if (lit.Token.Value is int i) return i;
            if (int.TryParse(lit.Token.ValueText, out var parsed)) return parsed;
        }
        return null;
    }
}
