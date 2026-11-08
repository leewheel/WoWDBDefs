using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Text;
using DBDefsLib;
using DBDefsLib.Structs;

namespace WowDBEditor.Core;

public enum WdbcColumnKind
{
    Int,
    UInt,
    Float,
    String,
    Mask
}

/// <summary>WDBC 的列（每个列固定占 4 字节，locstring 会展开成多列）。</summary>
public sealed class WdbcColumn
{
    public required string Key { get; init; }
    public required string BaseName { get; init; }
    public required int FieldIndex { get; init; }
    /// <summary>记录内的字节偏移（字段宽度不一定是 4 字节，例如 CharBaseInfo 只有 2 字节）。</summary>
    public required int ByteOffset { get; init; }
    /// <summary>字段宽度（字节）：1 / 2 / 4 / 8。</summary>
    public int Width { get; init; } = 4;
    public required WdbcColumnKind Kind { get; init; }
    public bool IsId { get; init; }
    public bool IsArray { get; init; }
    public int ArrayIndex { get; init; } = -1;
    public string DbType { get; init; } = "int";
    public bool Verified { get; init; } = true;
    public string Comment { get; init; }
    public string ForeignTable { get; init; }
    public string ForeignColumn { get; init; }

    public Type FieldType => Kind switch
    {
        WdbcColumnKind.Float => typeof(float),
        WdbcColumnKind.String => typeof(string),
        WdbcColumnKind.Mask => typeof(uint),
        WdbcColumnKind.UInt => Width switch
        {
            1 => typeof(byte),
            2 => typeof(ushort),
            8 => typeof(ulong),
            _ => typeof(uint)
        },
        _ => Width switch
        {
            1 => typeof(sbyte),
            2 => typeof(short),
            8 => typeof(long),
            _ => typeof(int)
        }
    };
}

/// <summary>
/// WDBC（3.3.5 及更早）的自研读写。
///
/// 为什么不用 DBCD：它按 record 类型逐字段读，遇到 locstring 时只消费 16 个 locale 槽位、
/// 跳过紧随其后的 mask，导致从第一个 locstring 起整条记录错位 4 字节（有四个 locstring 就错 16 字节）。
/// 表里 locstring 恰好都在靠后位置时看着"能用"，Spell 这种中间就有 locstring 的表直接读出垃圾。
/// WDBC 结构非常简单（定长 4 字节字段 + 字符串块），这里直接按偏移读写，结果与文件逐字节一致。
/// </summary>
public sealed class WdbcTable
{
    public const int HeaderSize = 20;
    private const string Magic = "WDBC";

    private readonly Dictionary<int, string> stringCache = new();

    public string FilePath { get; private set; }
    public int RecordsCount { get; private set; }
    public int FieldsCount { get; private set; }
    public int RecordSize { get; private set; }
    public byte[] Records { get; private set; }
    public byte[] StringBlock { get; private set; }
    public IReadOnlyList<WdbcColumn> Columns { get; private set; }
    public VersionDefinitions Version { get; private set; }
    public string BuildString { get; private set; }
    public WdbcColumn IdColumn { get; private set; }
    /// <summary>locstring 实际占用的连续 4 字节槽位数（含末尾 mask）。</summary>
    public int LocaleWidth { get; private set; }

    /// <summary>没有可用定义，当前是"原始数值"视图。</summary>
    public bool IsGeneric { get; private set; }
    public string UnresolvedReason { get; private set; }

    private WdbcTable()
    {
    }

    public static bool IsWdbc(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 && Encoding.ASCII.GetString(magic) == Magic;
        }
        catch
        {
            return false;
        }
    }

    public static WdbcTable Load(string path, DBDefinition? definition, string build = null, bool allowGeneric = true)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderSize || Encoding.ASCII.GetString(bytes, 0, 4) != Magic)
            throw new InvalidDataException("不是 WDBC 文件。");

        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        var fields = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var recordSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        var stringSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));

        if (count < 0 || fields <= 0 || recordSize <= 0)
            throw new InvalidDataException($"文件头异常：{count} 行 / {fields} 字段 / 记录 {recordSize} 字节。");

        var recordsBytes = count * recordSize;
        if (HeaderSize + recordsBytes > bytes.Length)
            throw new InvalidDataException("文件被截断，记录区长度不足。");

        var stringStart = HeaderSize + recordsBytes;
        var available = bytes.Length - stringStart;
        if (stringSize < 0 || stringSize > available)
            stringSize = available;

        var table = new WdbcTable
        {
            FilePath = path,
            RecordsCount = count,
            FieldsCount = fields,
            RecordSize = recordSize,
            Records = bytes.AsSpan(HeaderSize, recordsBytes).ToArray(),
            StringBlock = bytes.AsSpan(stringStart, stringSize).ToArray()
        };

        if (definition != null)
        {
            try
            {
                table.ResolveVersion(definition.Value, build);
                table.BuildColumns(definition.Value);
                return table;
            }
            catch (Exception) when (allowGeneric)
            {
                // 定义与实际结构对不上（魔改表、字段被改宽等）：退回通用视图，
                // 让用户至少能打开、查看和修改原始数值，而不是直接拒之门外。
                table.IsGeneric = true;
                table.UnresolvedReason = "定义与文件结构对不上";
            }
        }
        else
        {
            table.IsGeneric = true;
            table.UnresolvedReason = "缺少对应的 .dbd 定义文件";
        }

        table.BuildGenericColumns();
        return table;
    }

    /// <summary>没有可用定义，按固定宽度把记录切成若干整数列。</summary>
    private void BuildGenericColumns()
    {
        var width = FieldsCount > 0 && RecordSize % FieldsCount == 0 ? RecordSize / FieldsCount : 0;
        if (width is not (1 or 2 or 4 or 8))
        {
            throw new InvalidDataException(
                $"没有可用的定义，且无法把每条 {RecordSize} 字节均分成 {FieldsCount} 个字段。");
        }

        var columns = new List<WdbcColumn>(FieldsCount);
        for (var i = 0; i < FieldsCount; i++)
        {
            columns.Add(new WdbcColumn
            {
                Key = i == 0 ? "ID" : $"Field_{i}",
                BaseName = i == 0 ? "ID" : $"Field_{i}",
                FieldIndex = i,
                ByteOffset = i * width,
                Width = width,
                Kind = i == 0 ? WdbcColumnKind.Int : WdbcColumnKind.Int,
                IsId = i == 0,
                DbType = "int",
                Verified = false,
                Comment = "无定义文件，只能按原始数值查看/编辑"
            });
        }

        Columns = columns;
        IdColumn = columns[0];
        LocaleWidth = 0;
        Version = default;
        BuildString = null;
    }

    #region 版本与列

    /// <summary>
    /// WDBC 头里没有 LayoutHash，但有字段数。按"定义展开后的列数 == 文件字段数"匹配版本，
    /// 比记录字节数更精确（字节数相同的版本很多，字段数几乎不会撞车）。
    /// </summary>
    private void ResolveVersion(DBDefinition definition, string build)
    {
        if (!string.IsNullOrWhiteSpace(build))
        {
            try
            {
                Utils.GetVersionDefinitionByBuild(definition, new Build(build), out var requested);
                if (requested != null)
                {
                    var probe = RecordSizeEstimator.MaxBuildAtMost(requested.Value, RecordSizeEstimator.WdbcMaxBuild)
                                ?? RecordSizeEstimator.MaxBuild(requested.Value);
                    var (ok, width) = ComputeLayout(definition, requested.Value, probe);
                    if (ok)
                    {
                        Version = requested.Value;
                        BuildString = build;
                        LocaleWidth = width;
                        return;
                    }
                }
            }
            catch
            {
                // 版本号格式不合法就继续自动判断
            }
        }

        // WDBC 只可能是 3.3.5(12340) 及更早，先用格式上限排掉 4.x/现代版本，
        // 否则 4.0 的 locstring 只有 2 列，靠"列数凑巧相等"很容易误判成 3.3.5。
        var matches = new List<(VersionDefinitions Version, int LocaleWidth)>();
        foreach (var candidate in definition.versionDefinitions)
        {
            var probe = RecordSizeEstimator.MaxBuildAtMost(candidate, RecordSizeEstimator.WdbcMaxBuild);
            if (probe == null)
                continue;
            var (ok, width) = ComputeLayout(definition, candidate, probe);
            if (ok)
                matches.Add((candidate, width));
        }

        if (matches.Count == 0)
        {
            // 极端情况：定义里全都不早于 4.0，退回按字段数匹配
            foreach (var candidate in definition.versionDefinitions)
            {
                var probe = RecordSizeEstimator.MaxBuild(candidate);
                var (ok, width) = ComputeLayout(definition, candidate, probe);
                if (ok)
                    matches.Add((candidate, width));
            }
        }

        if (matches.Count == 0)
        {
            throw new DefinitionNotFoundException(Path.GetFileNameWithoutExtension(FilePath), true,
                $"文件头是 {FieldsCount} 个字段 / 每条 {RecordSize} 字节，定义里没有任何版本能对上。")
            {
                FileFormat = Magic,
                FileRecordSize = RecordSize
            };
        }

        var best = matches
            .Select(m => new
            {
                m.Version,
                m.LocaleWidth,
                Build = RecordSizeEstimator.MaxBuildAtMost(m.Version, RecordSizeEstimator.WdbcMaxBuild)
                        ?? RecordSizeEstimator.MaxBuild(m.Version)
            })
            .OrderByDescending(m => m.Build)
            .First();

        Version = best.Version;
        LocaleWidth = best.LocaleWidth;
        BuildString = best.Build?.ToString();
    }

    /// <summary>
    /// 推算某个版本的记录布局。locstring 的宽度不写死在代码里，
    /// 而是用"记录字节数 - 其它字段字节数"除以 locstring 个数反算，再与版本规则核对。
    /// </summary>
    private (bool Ok, int Width) ComputeLayout(DBDefinition definition, VersionDefinitions version, Build probeBuild)
    {
        var fixedBytes = 0;
        var localeCount = 0;

        foreach (var field in version.definitions)
        {
            var type = definition.columnDefinitions.TryGetValue(field.name, out var columnDef)
                ? columnDef.type ?? "int"
                : "int";

            if (string.Equals(type, "locstring", StringComparison.OrdinalIgnoreCase))
            {
                localeCount++;
                continue;
            }

            var count = field.isNonInline ? 1 : field.arrLength > 0 ? field.arrLength : 1;
            fixedBytes += count * WidthOf(type, field.size);
        }

        if (localeCount == 0)
            return (fixedBytes == RecordSize, 0);

        var remaining = RecordSize - fixedBytes;
        if (remaining <= 0)
            return (false, 0);

        var perLocaleBytes = remaining / localeCount;
        if (perLocaleBytes <= 0 || perLocaleBytes % 4 != 0)
            return (false, 0);

        var width = perLocaleBytes / 4;
        var expected = RecordSizeEstimator.LocStringSize(probeBuild) + 1;
        return width == expected ? (true, width) : (false, 0);
    }

    /// <summary>字段占几个字节：DBD 里的 &lt;32&gt; 是位数，字符串系列固定 4 字节偏移。</summary>
    private static int WidthOf(string type, int bitSize)
    {
        if (type is "string" or "locstring")
            return 4;
        var bits = bitSize > 0 ? bitSize : 32;
        return bits / 8 is 1 or 2 or 4 or 8 ? bits / 8 : 4;
    }

    private void BuildColumns(DBDefinition definition)
    {
        var columns = new List<WdbcColumn>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var width = ComputeLocaleWidth(definition);

        string UniqueKey(string candidate)
        {
            var key = candidate;
            var n = 2;
            while (!usedKeys.Add(key))
                key = candidate + "_" + n++;
            return key;
        }

        var byteOffset = 0;
        var fieldIndex = 0;

        foreach (var field in Version.definitions)
        {
            definition.columnDefinitions.TryGetValue(field.name, out var columnDef);
            var type = (columnDef.type ?? "int").ToLowerInvariant();
            var count = field.isNonInline ? 1 : field.arrLength > 0 ? field.arrLength : 1;

            if (type == "locstring")
            {
                for (var i = 0; i < width; i++)
                {
                    var isMask = i == width - 1;
                    columns.Add(new WdbcColumn
                    {
                        Key = UniqueKey(isMask ? field.name + "_mask" : $"{field.name}_{i}"),
                        BaseName = field.name,
                        FieldIndex = fieldIndex++,
                        ByteOffset = byteOffset,
                        Width = 4,
                        Kind = isMask ? WdbcColumnKind.Mask : WdbcColumnKind.String,
                        IsArray = true,
                        ArrayIndex = i,
                        DbType = "locstring",
                        Verified = columnDef.verified,
                        Comment = columnDef.comment ?? field.comment,
                        ForeignTable = columnDef.foreignTable,
                        ForeignColumn = columnDef.foreignColumn
                    });
                    byteOffset += 4;
                }
                LocaleWidth = width;
                continue;
            }

            var itemWidth = WidthOf(type, field.size);
            for (var i = 0; i < count; i++)
            {
                columns.Add(new WdbcColumn
                {
                    Key = UniqueKey(count > 1 ? $"{field.name}_{i}" : field.name),
                    BaseName = field.name,
                    FieldIndex = fieldIndex++,
                    ByteOffset = byteOffset,
                    Width = itemWidth,
                    Kind = type == "float" ? WdbcColumnKind.Float
                        : type == "string" ? WdbcColumnKind.String
                        : field.isSigned ? WdbcColumnKind.Int : WdbcColumnKind.UInt,
                    IsId = field.isID && i == 0,
                    IsArray = count > 1,
                    ArrayIndex = count > 1 ? i : -1,
                    DbType = type,
                    Verified = columnDef.verified,
                    Comment = columnDef.comment ?? field.comment,
                    ForeignTable = columnDef.foreignTable,
                    ForeignColumn = columnDef.foreignColumn
                });
                byteOffset += itemWidth;
            }
        }

        if (byteOffset != RecordSize)
        {
            throw new InvalidDataException(
                $"定义展开出 {columns.Count} 列 / {byteOffset} 字节，但文件头是 {FieldsCount} 个字段 / 每条 {RecordSize} 字节。");
        }

        Columns = columns;
        IdColumn = columns.FirstOrDefault(c => c.IsId) ?? columns.FirstOrDefault();
    }

    private int ComputeLocaleWidth(DBDefinition definition)
    {
        var fixedBytes = 0;
        var localeCount = 0;
        foreach (var field in Version.definitions)
        {
            var type = definition.columnDefinitions.TryGetValue(field.name, out var columnDef)
                ? columnDef.type ?? "int"
                : "int";
            if (string.Equals(type, "locstring", StringComparison.OrdinalIgnoreCase))
            {
                localeCount++;
                continue;
            }
            var count = field.isNonInline ? 1 : field.arrLength > 0 ? field.arrLength : 1;
            fixedBytes += count * WidthOf(type, field.size);
        }

        if (localeCount == 0)
            return 0;

        var remaining = RecordSize - fixedBytes;
        return remaining > 0 ? remaining / localeCount / 4 : 0;
    }

    #endregion

    #region 读取

    public string GetString(uint offset)
    {
        if (offset >= StringBlock.Length)
            return string.Empty;

        var start = (int)offset;
        if (stringCache.TryGetValue(start, out var cached))
            return cached;

        var end = Array.IndexOf(StringBlock, (byte)0, start);
        if (end < 0)
            end = StringBlock.Length;

        var text = Encoding.UTF8.GetString(StringBlock, start, end - start);
        stringCache[start] = text;
        return text;
    }

    public DataTable CreateDataTable(string tableName)
    {
        var table = new DataTable(tableName) { Locale = CultureInfo.InvariantCulture };
        foreach (var column in Columns)
            table.Columns.Add(column.Key, column.FieldType);

        // 注意：这里故意不设主键。WDBC 是按顺序排列的记录数组，并不要求 ID 唯一，
        // 魔改端的数据里确实存在重复 ID，设主键会让整表打不开。
        var columns = Columns as WdbcColumn[] ?? Columns.ToArray();
        // 绝大多数 WDBC 全是 32 位字段，用 uint 视图零拷贝快得多；
        // 少数表（CharBaseInfo 之类）有 8/16 位字段，走按字节读取。
        var allFourBytes = columns.All(c => c.Width == 4) && RecordSize == FieldsCount * 4;
        var words = allFourBytes
            ? System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(Records.AsSpan())
            : default;
        var stride = RecordSize / 4;

        table.BeginLoadData();
        var values = new object[columns.Length];
        for (var rowIndex = 0; rowIndex < RecordsCount; rowIndex++)
        {
            var baseIndex = rowIndex * stride;
            var byteBase = rowIndex * RecordSize;
            for (var i = 0; i < columns.Length; i++)
            {
                var column = columns[i];
                if (allFourBytes)
                {
                    var raw = words[baseIndex + column.FieldIndex];
                    values[i] = column.Kind switch
                    {
                        WdbcColumnKind.Float => BitConverter.UInt32BitsToSingle(raw),
                        WdbcColumnKind.String => GetString(raw),
                        WdbcColumnKind.UInt or WdbcColumnKind.Mask => raw,
                        _ => unchecked((int)raw)
                    };
                }
                else
                {
                    values[i] = ReadValue(Records.AsSpan(byteBase, RecordSize), column);
                }
            }
            table.Rows.Add(values);
        }
        table.EndLoadData();
        table.AcceptChanges();
        return table;
    }

    private object ReadValue(ReadOnlySpan<byte> record, WdbcColumn column)
    {
        var span = record[column.ByteOffset..];
        switch (column.Kind)
        {
            case WdbcColumnKind.Float:
                return BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(span));
            case WdbcColumnKind.String:
                return GetString(BinaryPrimitives.ReadUInt32LittleEndian(span));
            case WdbcColumnKind.Mask:
                return BinaryPrimitives.ReadUInt32LittleEndian(span);
            case WdbcColumnKind.UInt:
                return column.Width switch
                {
                    1 => span[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(span),
                    8 => BinaryPrimitives.ReadUInt64LittleEndian(span),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(span)
                };
            default:
                return column.Width switch
                {
                    1 => (sbyte)span[0],
                    2 => BinaryPrimitives.ReadInt16LittleEndian(span),
                    8 => BinaryPrimitives.ReadInt64LittleEndian(span),
                    _ => BinaryPrimitives.ReadInt32LittleEndian(span)
                };
        }
    }

    /// <summary>取某行的 ID 值（未找到 ID 列时返回行号 + 1）。</summary>
    public int GetRowId(DataTable table, DataRow row, int fallback)
    {
        if (IdColumn == null)
            return fallback;
        try
        {
            return Convert.ToInt32(row[IdColumn.Key], CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    #endregion

    #region 写入

    public void Save(DataTable table, string path)
    {
        var rows = table.Rows.Cast<DataRow>().Where(r => r.RowState != DataRowState.Deleted).ToList();
        var recordSize = RecordSize;
        var records = new byte[rows.Count * recordSize];

        // 在原字符串块后面追加，已存在的字符串复用原偏移，保证没改过的内容逐字节不变
        var builder = new List<byte>(StringBlock.Length + 1024);
        builder.AddRange(StringBlock);
        var offsets = BuildStringIndex(StringBlock);

        uint ResolveString(string text)
        {
            text ??= string.Empty;
            // 只有真的要写字符串时才初始化字符串块，纯整数表保持 0 字节
            if (builder.Count == 0)
                builder.Add(0);
            if (offsets.TryGetValue(text, out var existing))
                return (uint)existing;

            var start = builder.Count;
            var bytes = Encoding.UTF8.GetBytes(text);
            builder.AddRange(bytes);
            builder.Add(0);
            offsets[text] = start;
            return (uint)start;
        }

        // 行数与顺序没变时，未修改的行直接原样复制。这样即使某些字符串偏移指向字符串中间
        // （魔改端常见），没动过的数据也保持逐字节不变，保存不会"看起来改了"。
        var sameShape = rows.Count == RecordsCount &&
                        rows.All(r => r.RowState is DataRowState.Unchanged or DataRowState.Modified);

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var record = records.AsSpan(rowIndex * recordSize, recordSize);

            if (sameShape && row.RowState == DataRowState.Unchanged)
            {
                Records.AsSpan(rowIndex * recordSize, recordSize).CopyTo(record);
                continue;
            }

            var original = sameShape ? Records.AsSpan(rowIndex * recordSize, recordSize) : default;
            foreach (var column in Columns)
            {
                var value = row[column.Key];
                var target = record[column.ByteOffset..];

                if (column.Kind == WdbcColumnKind.String)
                {
                    var text = value == DBNull.Value
                        ? string.Empty
                        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

                    // 内容没变就沿用原偏移，避免把字符串块重排
                    if (sameShape)
                    {
                        var oldOffset = BinaryPrimitives.ReadUInt32LittleEndian(original[column.ByteOffset..]);
                        if (GetString(oldOffset) == text)
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(target, oldOffset);
                            continue;
                        }
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(target, ResolveString(text));
                    continue;
                }

                if (column.Kind == WdbcColumnKind.Float)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(target, BitConverter.SingleToUInt32Bits(
                        value == DBNull.Value ? 0f : Convert.ToSingle(value, CultureInfo.InvariantCulture)));
                    continue;
                }

                WriteInteger(target, column, value);
            }
        }

        var stringBlock = builder.ToArray();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var tempPath = Path.Combine(directory,
            "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");

        try
        {
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var header = new byte[HeaderSize];
                Encoding.ASCII.GetBytes(Magic, header);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), rows.Count);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), FieldsCount);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), recordSize);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), stringBlock.Length);
                output.Write(header);
                output.Write(records);
                output.Write(stringBlock);
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tempPath, path, null);
                }
                catch (IOException)
                {
                    File.Move(tempPath, path, true);
                }
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }

        FilePath = path;
        Records = records;
        StringBlock = stringBlock;
        RecordsCount = rows.Count;
        stringCache.Clear();
        table.AcceptChanges();
    }

    private static void WriteInteger(Span<byte> span, WdbcColumn column, object value)
    {
        var isUnsigned = column.Kind is WdbcColumnKind.UInt or WdbcColumnKind.Mask;
        if (isUnsigned)
        {
            var raw = value == DBNull.Value ? 0UL : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            switch (column.Width)
            {
                case 1: span[0] = (byte)raw; break;
                case 2: BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)raw); break;
                case 8: BinaryPrimitives.WriteUInt64LittleEndian(span, raw); break;
                default: BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)raw); break;
            }
        }
        else
        {
            var raw = value == DBNull.Value ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            switch (column.Width)
            {
                case 1: span[0] = unchecked((byte)(sbyte)raw); break;
                case 2: BinaryPrimitives.WriteInt16LittleEndian(span, (short)raw); break;
                case 8: BinaryPrimitives.WriteInt64LittleEndian(span, raw); break;
                default: BinaryPrimitives.WriteInt32LittleEndian(span, (int)raw); break;
            }
        }
    }

    private static Dictionary<string, int> BuildStringIndex(byte[] block)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var start = 0;
        for (var i = 0; i < block.Length; i++)
        {
            if (block[i] != 0)
                continue;
            var text = Encoding.UTF8.GetString(block, start, i - start);
            // 同一个字符串可能被存了多份，保留最靠前的那份偏移
            if (!map.ContainsKey(text))
                map[text] = start;
            start = i + 1;
        }
        return map;
    }

    #endregion
}
