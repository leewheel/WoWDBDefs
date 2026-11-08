using DBDefsLib;
using DBDefsLib.Structs;

namespace WowDBEditor.Core;

/// <summary>
/// 按 DBD 定义推算一条记录占多少字节，用来和文件头里的 RecordSize 比对。
/// 3.3.5 这类老格式每个字段固定 4 字节，locstring 展开成 8/16 个语言列，因此这个指纹相当可靠。
/// </summary>
public static class RecordSizeEstimator
{
    /// <summary>WDBC 格式存在的最晚版本（3.3.5.12340）。注意要按版本四段比较，
    /// 不能只比 build 号：4.0.0.11792 的 build 号比 3.3.5.12340 还小。</summary>
    public static readonly Build WdbcMaxBuild = new("3.3.5.12340");

    public static int Estimate(DBDefinition definition, VersionDefinitions version, Build forBuild = null)
    {
        var locSize = LocStringSize(forBuild ?? MaxBuild(version));
        var total = 0;
        foreach (var field in version.definitions)
        {
            var column = definition.columnDefinitions.TryGetValue(field.name, out var found) ? found : default;
            var count = field.arrLength > 0 ? field.arrLength : 1;

            if (field.isNonInline)
            {
                total += 4; // 非内联关系只存一个偏移
                continue;
            }

            total += column.type switch
            {
                "int" => (field.size > 0 ? field.size / 8 : 4) * count,
                "float" => 4 * count,
                "string" => 4 * count,
                // 4.0 之前每个 locstring 展开成 locSize 个语言列，DBCD 还会额外生成一个 uint mask 字段
                "locstring" => 4 * count * (locSize > 1 ? locSize : 1) + (locSize > 1 ? 4 : 0),
                _ => 4 * count
            };
        }
        return total;
    }

    /// <summary>与 DBCDBuilder 一致的本地化字符串列数规则（4.0 之前是 8/16 个语言列）。</summary>
    public static int LocStringSize(Build build)
    {
        if (build == null)
            return 1;
        if (build.expansion >= 4 || build.build > 12340)
            return 1;
        return build.build >= 6692 ? 16 : 8;
    }

    /// <summary>区间/列表中不超过给定版本号的最大版本（用于 WDBC 只可能是 3.3.5 及更早这一约束）。</summary>
    public static Build MaxBuildAtMost(VersionDefinitions version, Build ceiling)
    {
        Build best = null;
        foreach (var build in version.builds ?? Array.Empty<Build>())
        {
            if (build.CompareTo(ceiling) > 0) continue;
            if (best == null || build.CompareTo(best) > 0) best = build;
        }
        foreach (var range in version.buildRanges ?? Array.Empty<BuildRange>())
        {
            var candidate = range.maxBuild.CompareTo(ceiling) > 0 ? range.minBuild : range.maxBuild;
            if (candidate.CompareTo(ceiling) > 0) continue;
            if (best == null || candidate.CompareTo(best) > 0) best = candidate;
        }
        return best;
    }

    public static Build MaxBuild(VersionDefinitions version)
    {
        Build best = null;
        if (version.builds != null)
        {
            foreach (var build in version.builds)
            {
                if (best == null || build.CompareTo(best) > 0)
                    best = build;
            }
        }
        if (version.buildRanges != null)
        {
            foreach (var range in version.buildRanges)
            {
                if (best == null || range.maxBuild.CompareTo(best) > 0)
                    best = range.maxBuild;
            }
        }
        return best;
    }
}
