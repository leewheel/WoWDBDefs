using System.Reflection;

namespace WowDBEditor.Core;

public sealed class DbColumn
{
    public required string Key { get; init; }
    public required string BaseName { get; init; }
    public required Type FieldType { get; init; }
    public required FieldInfo Field { get; init; }
    public bool IsArray { get; init; }
    public int ArrayIndex { get; init; } = -1;
    public bool IsId { get; init; }
    public string DbType { get; init; } = "int";
    public bool Verified { get; init; } = true;
    public string Comment { get; init; }
    public string ForeignTable { get; init; }
    public string ForeignColumn { get; init; }

    public string TypeLabel => FieldType.Name.ToLowerInvariant() switch
    {
        "sbyte" => "int8",
        "byte" => "uint8",
        "int16" => "int16",
        "uint16" => "uint16",
        "int32" => "int32",
        "uint32" => "uint32",
        "int64" => "int64",
        "uint64" => "uint64",
        "single" => "float",
        "string" => DbType == "locstring" ? "locstring" : "string",
        _ => FieldType.Name
    };
}