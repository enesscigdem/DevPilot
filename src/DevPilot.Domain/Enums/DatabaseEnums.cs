namespace DevPilot.Domain.Enums;

public enum DatabaseObjectType
{
    Unknown = 0,
    Table = 1,
    Column = 2,
    Index = 3,
    Constraint = 4,
    Relationship = 5
}

public enum DatabaseChangeOperation
{
    Unknown = 0,
    Add = 1,
    Remove = 2,
    Alter = 3,
    Rename = 4
}

public enum DatabaseChangeKind
{
    None = 0,
    Additive = 1,
    PotentiallyDataSensitive = 2,
    Destructive = 3,
    Unknown = 4
}

public enum DatabaseMigrationRequirement
{
    None = 0,
    Expected = 1,
    Possible = 2,
    ReviewRequired = 3
}

public enum DataMigrationRequirement
{
    None = 0,
    ReviewRequired = 1,
    Required = 2,
    Possible = 3
}
