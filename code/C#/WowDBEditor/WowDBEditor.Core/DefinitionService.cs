using DBDefsLib;
using DBDefsLib.Structs;

namespace WowDBEditor.Core;

public sealed class DefinitionNotFoundException : Exception
{
    public string TableName { get; }
    public bool DbdExists { get; }

    /// <summary>文件格式标识，例如 WDBC / WDC5，便于界面按格式裁剪版本列表。</summary>
    public string FileFormat { get; init; }

    /// <summary>文件头里的记录长度，用于在版本列表中标注哪些条目长度吻合。</summary>
    public int FileRecordSize { get; init; }

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

    /// <summary>一个可选的版本条目：区间版本会额外给出说明，方便用户认出自己客户端对应的那一行。</summary>
    public sealed record VersionChoice(string Label, string Build, string Detail, bool IsRange, string LayoutHint,
        int RecordSize, bool MatchesFile);

    /// <summary>
    /// 列出可选的版本。
    /// </summary>
    /// <param name="definition">表定义</param>
    /// <param name="fileFormat">文件格式标识（WDBC / WDB2 / WDC5 …），用于裁掉不可能的版本</param>
    /// <param name="fileRecordSize">文件头里的记录长度；大于 0 时会在列表里标出长度吻合的条目</param>
    public List<VersionChoice> ListVersionChoices(DBDefinition definition, string fileFormat = null,
        int fileRecordSize = 0)
    {
        // WDBC 只存在于 3.3.5 (12340) 及更早。注意 DBD 里同一段定义常常同时覆盖 3.3.5 和 4.0.x，
        // 所以必须按“条目”逐个过滤，不能按整块的最大版本过滤，否则 3.3.5 会被一起删掉。
        var isWdbc = string.Equals(fileFormat, "WDBC", StringComparison.OrdinalIgnoreCase);
        var wdbcMaxBuild = RecordSizeEstimator.WdbcMaxBuild;

        var choices = new List<VersionChoice>();
        foreach (var version in definition.versionDefinitions)
        {
            var layouts = version.layoutHashes is { Length: > 0 }
                ? string.Join(", ", version.layoutHashes)
                : null;

            foreach (var build in version.builds)
            {
                if (isWdbc && build.CompareTo(wdbcMaxBuild) > 0)
                    continue;
                choices.Add(MakeChoice(definition, version, $"BUILD {build}", build, false, layouts,
                    fileRecordSize, $"精确版本 {build}"));
            }

            foreach (var range in version.buildRanges)
            {
                if (isWdbc && range.minBuild.CompareTo(wdbcMaxBuild) > 0)
                    continue;
                // 区间条目：DBD 里整段版本共用一个定义，区间内任意版本号都等价，取上界最贴近当前客户端
                choices.Add(MakeChoice(definition, version,
                    $"BUILD {range.minBuild} ~ {range.maxBuild}（区间）", range.maxBuild, true, layouts,
                    fileRecordSize,
                    $"该定义适用于 {range.minBuild} ~ {range.maxBuild} 之间的所有客户端版本，\n" +
                    $"选它等于选 {range.maxBuild}（区间内任意版本等价）"));
            }
        }

        if (choices.Count == 0 && isWdbc)
            return ListVersionChoices(definition, null, fileRecordSize); // 定义里没有早于 4.0 的条目时退回全量

        return choices
            .OrderByDescending(c => c.MatchesFile)
            .ThenByDescending(c => ParseBuild(c.Build))
            .ToList();
    }

    private static VersionChoice MakeChoice(DBDefinition definition, VersionDefinitions version, string label,
        Build build, bool isRange, string layouts, int fileRecordSize, string description)
    {
        // 记录长度按“这个条目自己的版本”算：同一个定义块里 3.3.5 和 4.0 的 locstring 宽度不同
        var recordSize = RecordSizeEstimator.Estimate(definition, version, build);
        var matches = fileRecordSize > 0 && recordSize == fileRecordSize;

        var detail = description +
                     $"\n记录长度: {recordSize} 字节" +
                     (matches ? "（与文件一致 ✔）" : "") +
                     (layouts != null ? $"\nLayoutHash: {layouts}" : "");
        if (matches)
            label = "✔ " + label;

        return new VersionChoice(label, build.ToString(), detail, isRange, layouts, recordSize, matches);
    }

    private static Version ParseBuild(string build)
    {
        try
        {
            return new Version(build);
        }
        catch
        {
            return new Version(0, 0, 0);
        }
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