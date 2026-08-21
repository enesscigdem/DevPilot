using DevPilot.Domain.Enums;

namespace DevPilot.Domain.ValueObjects;

public sealed class DatabaseChange
{
    public DatabaseObjectType ObjectType { get; set; } = DatabaseObjectType.Unknown;

    public string ObjectName { get; set; } = string.Empty;

    public string? ParentObjectName { get; set; }

    public DatabaseChangeOperation Operation { get; set; } = DatabaseChangeOperation.Unknown;

    public string? Before { get; set; }

    public string? After { get; set; }

    public RiskLevel Risk { get; set; } = RiskLevel.Low;

    public string Evidence { get; set; } = string.Empty;

    public string GetMatchKey()
    {
        var parent = (ParentObjectName ?? string.Empty).Trim().ToLowerInvariant();
        var obj = ObjectName.Trim().ToLowerInvariant();
        var type = ObjectType.ToString().ToLowerInvariant();
        var op = Operation.ToString().ToLowerInvariant();
        return $"{type}:{parent}:{obj}:{op}";
    }

    public bool Matches(DatabaseChange? other)
    {
        if (other is null) return false;

        // Structured matching across object type, name, parent (if specified), and operation
        if (ObjectType != DatabaseObjectType.Unknown && other.ObjectType != DatabaseObjectType.Unknown)
        {
            if (ObjectType != other.ObjectType) return false;
        }

        if (Operation != DatabaseChangeOperation.Unknown && other.Operation != DatabaseChangeOperation.Unknown)
        {
            if (Operation != other.Operation) return false;
        }

        if (!string.IsNullOrWhiteSpace(ObjectName) && !string.IsNullOrWhiteSpace(other.ObjectName))
        {
            if (!string.Equals(ObjectName.Trim(), other.ObjectName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(ParentObjectName) && !string.IsNullOrWhiteSpace(other.ParentObjectName))
        {
            var p1 = ParentObjectName.Trim();
            var p2 = other.ParentObjectName.Trim();
            if (!string.Equals(p1, p2, StringComparison.OrdinalIgnoreCase))
            {
                // Handle plural table vs singular entity name (e.g., "Order" vs "Orders")
                var norm1 = p1.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? p1[..^1] : p1;
                var norm2 = p2.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? p2[..^1] : p2;
                if (!string.Equals(norm1, norm2, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
