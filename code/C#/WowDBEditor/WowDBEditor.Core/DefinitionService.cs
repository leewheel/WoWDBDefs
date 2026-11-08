using DBDefsLib;
using DBDefsLib.Structs;

namespace WowDBEditor.Core;

public sealed class DefinitionNotFoundException : Exception
{
    public string TableName { get; }
    public bool DbdExists { get; }

    public DefinitionNotFoundException(string tableName, bool dbdExists, string message) : base(message)
    {
        TableName = tableName;
        DbdExists = dbdExists;
    }
}

public sealed class DefinitionService
{
    private readonly Dictionary<string, DBDefinition> cache = new(StringComparer.OrdinalIgnoreCase);

    public string DefinitionsDirectory { get; }

    public DefinitionService(string definitionsDirectory)
    {
        DefinitionsDirectory = definitionsDirectory;
    }

    public bool HasDefinition(string tableName) => File.Exists(DbdPath(tableName));

    public string DbdPath(string tableName) => Path.Combine(DefinitionsDirectory, tableName + ".dbd");

    public DBDefinition GetDefinition(string tableName)
    {
        if (cache.TryGetValue(tableName, out var cached))
            return cached;

        var path = DbdPath(tableName);
        if (!File.Exists(path))
            throw new DefinitionNotFoundException(tableName, false, $"未找到定义文件：{path}");

        var definition = new DBDReader().Read(path);
        cache[tableName] = definition;
        return definition;
    }

    public void InvalidateCache() => cache.Clear();

    public List<(string Label, string Build)> ListVersionChoices(DBDefinition definition)
    {
        var choices = new List<(string, string)>();
        foreach (var version in definition.versionDefinitions)
        {
            var layouts = version.layoutHashes is { Length: > 0 } ? "  [LAYOUT " + string.Join(", ", version.layoutHashes) + "]" : "";
            foreach (var build in version.builds)
                choices.Add(($"BUILD {build}{layouts}", build.ToString()));
            foreach (var range in version.buildRanges)
                choices.Add(($"BUILD {range.minBuild} ~ {range.maxBuild}{layouts}", range.minBuild.ToString()));
        }
        return choices;
    }

    public static string FindDefinitionsDirectory(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "definitions");
            if (File.Exists(Path.Combine(candidate, "Map.dbd")))
                return candidate;
        }
        return null;
    }
}