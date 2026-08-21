using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DevPilot.Infrastructure.DatabaseIntelligence;

public sealed record EntityPropertyInfo(
    string PropertyName,
    string TypeName,
    bool IsNullable,
    bool IsRequired,
    int? MaxLength,
    int? Precision,
    int? Scale,
    string? DefaultValue);

public sealed record EntityModelInfo(
    string EntityName,
    string? TableName,
    IReadOnlyList<EntityPropertyInfo> Properties);

public static class EfCoreEvidenceCollector
{
    public static EntityModelInfo? InspectEntityClass(string fileContent)
    {
        if (string.IsNullOrWhiteSpace(fileContent)) return null;

        var syntaxTree = CSharpSyntaxTree.ParseText(fileContent);
        var root = syntaxTree.GetRoot();

        var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classDecl == null) return null;

        var entityName = classDecl.Identifier.Text;
        string? tableName = null;

        // Check for [Table("...")] attribute
        var tableAttr = classDecl.AttributeLists
            .SelectMany(a => a.Attributes)
            .FirstOrDefault(a => a.Name.ToString().Contains("Table", StringComparison.OrdinalIgnoreCase));

        if (tableAttr?.ArgumentList?.Arguments.Count > 0)
        {
            var firstArg = tableAttr.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax lit && lit.Token.Value is string s)
            {
                tableName = s;
            }
        }

        var props = new List<EntityPropertyInfo>();
        var propertyDecls = classDecl.DescendantNodes().OfType<PropertyDeclarationSyntax>();

        foreach (var prop in propertyDecls)
        {
            var propName = prop.Identifier.Text;
            var typeName = prop.Type.ToString();
            var isNullable = typeName.EndsWith("?", StringComparison.Ordinal) ||
                             typeName.StartsWith("Nullable<", StringComparison.OrdinalIgnoreCase);

            var isRequired = prop.AttributeLists
                .SelectMany(a => a.Attributes)
                .Any(a => a.Name.ToString().Contains("Required", StringComparison.OrdinalIgnoreCase));

            // In C# 8+ nullable reference types, non-nullable string without ? is technically required unless defaulted
            if (!isNullable && (typeName == "string" || !typeName.EndsWith("?")) &&
                (typeName != "int" && typeName != "long" && typeName != "Guid" && typeName != "bool" && typeName != "decimal" && typeName != "double" && typeName != "DateTime"))
            {
                // Reference type without ?
                isRequired = true;
            }

            int? maxLength = null;
            var maxLengthAttr = prop.AttributeLists
                .SelectMany(a => a.Attributes)
                .FirstOrDefault(a => a.Name.ToString().Contains("MaxLength", StringComparison.OrdinalIgnoreCase) ||
                                     a.Name.ToString().Contains("StringLength", StringComparison.OrdinalIgnoreCase));

            if (maxLengthAttr?.ArgumentList?.Arguments.Count > 0)
            {
                var arg = maxLengthAttr.ArgumentList.Arguments[0].Expression;
                if (arg is LiteralExpressionSyntax lit && lit.Token.Value is int ml)
                {
                    maxLength = ml;
                }
            }

            string? defaultValue = prop.Initializer?.Value.ToString();

            props.Add(new EntityPropertyInfo(
                PropertyName: propName,
                TypeName: typeName,
                IsNullable: isNullable,
                IsRequired: isRequired,
                MaxLength: maxLength,
                Precision: null,
                Scale: null,
                DefaultValue: defaultValue));
        }

        return new EntityModelInfo(entityName, tableName, props);
    }
}
