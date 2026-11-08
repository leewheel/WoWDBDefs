using System.Collections;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DBCD;
using DBCD.IO;
using DBCD.IO.Attributes;
using DBDefsLib;
using DBDefsLib.Structs;

namespace WowDBEditor.Core;

public sealed class DbTable : IDisposable
{
    private readonly DBParser parser;
    private readonly WdbcTable wdbc;
    private readonly Type recordType;
    private readonly IDictionary storage;
    private readonly MethodInfo writeRecordsMethod;
    private readonly FieldInfo[] recordFields;
    private readonly int idFieldIndex;

    public string FilePath { get; private set; }
    public string TableName { get; }
    public string Format => wdbc != null ? "WDBC" : parser.Identifier;
    public uint LayoutHash => wdbc != null ? 0 : parser.LayoutHash;
    /// <summary>文件头里的记录字节数。</summary>
    public int RecordSize => wdbc != null ? wdbc.RecordSize : parser.RecordSize;
    /// <summary>按当前定义算出的记录字节数，与 <see cref="RecordSize"/> 不一致说明存在自定义列。</summary>
    public int DefinitionRecordSize { get; private set; }
    public uint TableHash => wdbc != null ? 0 : parser.TableHash;
    public DBDefinition Definition { get; }
    public VersionDefinitions Version { get; }
    public string BuildString { get; }
    public IReadOnlyList<DbColumn> Columns { get; }
    public DbColumn IdColumn { get; }
    public int RowCount => wdbc != null ? wdbc.RecordsCount : storage.Count;

    /// <summary>WDBC 走自研读写，locstring 的宽度（含 mask）由文件字段数反推。</summary>
    public int LocaleWidth => wdbc?.LocaleWidth ?? 0;

    /// <summary>没有可用定义，当前按"原始数值"打开。</summary>
    public bool IsGeneric => wdbc?.IsGeneric ?? false;
    public string UnresolvedReason => wdbc?.UnresolvedReason;

    private DbTable(string filePath, string tableName, DBParser parser, Type recordType, IDictionary storage,
        DBDefinition definition, VersionDefinitions version, string buildString)
    {
        FilePath = filePath;
        TableName = tableName;
        this.parser = parser;
        this.recordType = recordType;
        this.storage = storage;
        Definition = definition;
        Version = version;
        BuildString = buildString;

        recordFields = recordType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        writeRecordsMethod = typeof(DBParser).GetMethods()
            .First(m => m.Name == nameof(DBParser.WriteRecords)
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[1].ParameterType == typeof(Stream))
            .MakeGenericMethod(recordType);

        Build resolvedBuild = null;
        if (!string.IsNullOrWhiteSpace(buildString))
        {
            try { resolvedBuild = new Build(buildString); }
            catch { resolvedBuild = null; }
        }
        DefinitionRecordSize = RecordSizeEstimator.Estimate(definition, version, resolvedBuild);
        Columns = BuildColumns(version);

        var idField = recordFields.FirstOrDefault(f => f.IsDefined(typeof(IndexAttribute)))
                      ?? recordFields.FirstOrDefault(f => f.Name == "ID")
                      ?? recordFields.FirstOrDefault(f => f.FieldType == typeof(int));
        IdColumn = idField != null ? Columns.FirstOrDefault(c => c.Field == idField && c.ArrayIndex <= 0) : null;
        idFieldIndex = idField != null ? Array.IndexOf(recordFields, idField) : 0;
    }

    private DbTable(string filePath, string tableName, WdbcTable wdbc, DBDefinition definition,
        VersionDefinitions version, string buildString, IReadOnlyList<DbColumn> columns)
    {
        FilePath = filePath;
        TableName = tableName;
        this.wdbc = wdbc;
        Definition = definition;
        Version = version;
        BuildString = buildString;
        Columns = columns;
        IdColumn = wdbc.IdColumn != null ? columns.FirstOrDefault(c => c.Key == wdbc.IdColumn.Key) : null;
        recordFields = Array.Empty<FieldInfo>();
        DefinitionRecordSize = wdbc.RecordSize;
    }

    public static DbTable Open(string filePath, DefinitionService definitions, string build = null)
    {
        var tableName = Path.GetFileNameWithoutExtension(filePath);
        var isWdbc = WdbcTable.IsWdbc(filePath);

        DBDefinition definition = default;
        var hasDefinition = false;
        try
        {
            definition = definitions.GetDefinition(tableName);
            hasDefinition = true;
        }
        catch (DefinitionNotFoundException) when (isWdbc)
        {
            // WDBC 结构简单（定长字段 + 字符串块），即使没有定义也能按原始数值打开
        }

        // WDBC 用自研读写：DBCD 读 locstring 时会漏掉末尾的 mask 字段，导致整条记录错位
        if (isWdbc)
        {
            var wdbc = WdbcTable.Load(filePath, hasDefinition ? definition : null, build, allowGeneric: true);
            var columns = wdbc.Columns.Select(c => new DbColumn
            {
                Key = c.Key,
                BaseName = c.BaseName,
                FieldType = c.FieldType,
                Field = null,
                IsArray = c.IsArray,
                ArrayIndex = c.ArrayIndex,
                IsId = c.IsId,
                DbType = c.DbType,
                Verified = c.Verified,
                Comment = c.Comment,
                ForeignTable = c.ForeignTable,
                ForeignColumn = c.ForeignColumn
            }).ToList();

            return new DbTable(filePath, tableName, wdbc, definition, wdbc.Version, wdbc.BuildString, columns);
        }

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var parser = new DBParser(stream);

        var version = ResolveVersion(definition, parser, tableName, build, out var effectiveBuild);

        var builder = new DBCDBuilder();
        var (recordType, _) = builder.Build(parser, definition, SanitizeTypeName(tableName), effectiveBuild);

        var storage = ReadRecords(parser, recordType);

        return new DbTable(filePath, tableName, parser, recordType, storage, definition, version, effectiveBuild);
    }

    /// <summary>
    /// 读记录。部分文件（如魔改端导出的 Spell.dbc）里某些字符串/掩码偏移指向字符串块中间，
    /// DBCD 用 Dictionary 查不到就会抛 KeyNotFoundException，整表打不开。
    /// 这类脏数据不应该阻断打开：把缺失的偏移补成空串再重试即可。
    /// </summary>
    private static IDictionary ReadRecords(DBParser parser, Type recordType)
    {
        var getRecords = typeof(DBParser).GetMethod(nameof(DBParser.GetRecords), Type.EmptyTypes)
            .MakeGenericMethod(recordType);

        for (var attempt = 0; attempt < 32; attempt++)
        {
            try
            {
                return (IDictionary)getRecords.Invoke(parser, null);
            }
            catch (Exception ex)
            {
                var key = ExtractMissingKey(ex);
                if (key == null || !AddEmptyString(parser, key.Value))
                    throw;
            }
        }

        return (IDictionary)getRecords.Invoke(parser, null);
    }

    /// <summary>从异常链里取出 "The given key '123' was not present in the dictionary." 里的数字。</summary>
    private static long? ExtractMissingKey(Exception exception)
    {
        for (Exception current = exception; current != null; current = current.InnerException)
        {
            if (current is not KeyNotFoundException)
                continue;
            var match = MissingKeyPattern.Match(current.Message ?? string.Empty);
            if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var key))
                return key;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                var nested = ExtractMissingKey(inner);
                if (nested != null)
                    return nested;
            }
        }

        return null;
    }

    private static readonly Regex MissingKeyPattern = new(@"'(-?\d+)'", RegexOptions.Compiled);

    private static FieldInfo stringTableField;
    private static FieldInfo readerField;

    /// <summary>往 DBCD 的字符串表里补一个空串占位，让读取能继续。</summary>
    private static bool AddEmptyString(DBParser parser, long offset)
    {
        try
        {
            readerField ??= typeof(DBParser).GetField("_reader", BindingFlags.Instance | BindingFlags.NonPublic);
            var reader = readerField?.GetValue(parser);
            if (reader == null)
                return false;

            if (stringTableField == null)
            {
                // StringTable 是 BaseReader 上的私有字段，GetField 不会返回基类私有成员，得沿继承链找
                for (var type = reader.GetType(); type != null && stringTableField == null; type = type.BaseType)
                {
                    stringTableField = type.GetField("<StringTable>k__BackingField",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
            }

            if (stringTableField?.GetValue(reader) is not Dictionary<long, string> table)
                return false;

            if (table.ContainsKey(offset))
                return false;

            table[offset] = string.Empty;
            PatchedStringKeys.Add(offset);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>为了能继续读而补过占位的关键字偏移（仅用于提示，不影响数据）。</summary>
    public static readonly HashSet<long> PatchedStringKeys = new();

    /// <summary>
    /// 选版本：显式 BUILD → LayoutHash → 记录长度唯一匹配（老格式） → 定义里只有一个版本。
    /// 都判断不出来时抛 <see cref="DefinitionNotFoundException"/>，
    /// 由界面弹出「选择定义版本」对话框，而不是直接报底层错误。
    /// </summary>
    private static VersionDefinitions ResolveVersion(DBDefinition definition, DBParser parser, string tableName,
        string build, out string effectiveBuild)
    {
        effectiveBuild = build;
        VersionDefinitions? version = null;
        var isWdbc = string.Equals(parser.Identifier, "WDBC", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(build))
            Utils.GetVersionDefinitionByBuild(definition, new Build(build), out version);

        if (version == null && parser.LayoutHash != 0)
            Utils.GetVersionDefinitionByLayoutHash(definition, parser.LayoutHash.ToString("X8"), out version);

        // 老格式（WDBC/WDB2-WDB4）文件头没有 LayoutHash，但记录长度是很好的指纹：
        // 只有一个版本定义算出来的记录长度与之相符，就直接用它，省掉手工选版本。
        if (version == null && string.IsNullOrWhiteSpace(build) && parser.LayoutHash == 0 && parser.RecordSize > 0)
        {
            // WDBC 只可能是 3.3.5(12340) 及更早，用它排掉字节数碰巧相同的现代版本，避免误判
            var matches = new List<VersionDefinitions>();
            foreach (var candidate in definition.versionDefinitions)
            {
                var probeBuild = isWdbc
                    ? RecordSizeEstimator.MaxBuildAtMost(candidate, RecordSizeEstimator.WdbcMaxBuild)
                    : RecordSizeEstimator.MaxBuild(candidate);
                if (isWdbc && probeBuild == null)
                    continue; // 整个版本都晚于 3.3.5，不可能是 WDBC 文件
                if (RecordSizeEstimator.Estimate(definition, candidate, probeBuild) == parser.RecordSize)
                    matches.Add(candidate);
            }
            if (matches.Count == 1)
                version = matches[0];
        }

        if (version == null && string.IsNullOrWhiteSpace(build) && definition.versionDefinitions is { Length: 1 })
            version = definition.versionDefinitions[0];

        if (version == null)
            throw new DefinitionNotFoundException(tableName, true, "无法根据文件头判断定义版本，请手动选择客户端版本。")
            {
                FileFormat = parser.Identifier,
                FileRecordSize = parser.RecordSize
            };

        var resolved = version.Value;
        if (string.IsNullOrWhiteSpace(effectiveBuild))
        {
            // 区间版本取上界：DBD 里整段共用一个定义，上界最接近当前客户端。
            // 但同一块定义可能同时覆盖 3.3.5 和 4.0，WDBC 文件必须落在 3.3.5 以内，
            // 否则 locstring 宽度会按 4.0 算（1 列），整表列数就错了。
            var chosen = isWdbc
                ? RecordSizeEstimator.MaxBuildAtMost(resolved, RecordSizeEstimator.WdbcMaxBuild)
                : RecordSizeEstimator.MaxBuild(resolved);
            chosen ??= RecordSizeEstimator.MaxBuild(resolved);
            if (chosen != null)
                effectiveBuild = chosen.ToString();
        }

        return resolved;
    }

    private static string SanitizeTypeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0]))
            sb.Insert(0, '_');
        return sb.ToString();
    }

    private IReadOnlyList<DbColumn> BuildColumns(VersionDefinitions version)
    {
        var columns = new List<DbColumn>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in recordFields)
        {
            var baseName = field.Name.EndsWith("_mask") ? field.Name[..^5] : field.Name;
            Definition.columnDefinitions.TryGetValue(baseName, out var columnDef);
            var fieldDef = version.definitions.FirstOrDefault(d => d.name == baseName);

            var isArray = field.FieldType.IsArray;
            var elementType = isArray ? field.FieldType.GetElementType() : field.FieldType;
            var cardinality = field.GetCustomAttribute<CardinalityAttribute>()?.Count ?? 1;
            var isId = field.IsDefined(typeof(IndexAttribute));

            string UniqueKey(string candidate)
            {
                var key = candidate;
                var n = 2;
                while (!usedKeys.Add(key))
                    key = candidate + "_" + n++;
                return key;
            }

            if (isArray)
            {
                for (var i = 0; i < cardinality; i++)
                {
                    columns.Add(new DbColumn
                    {
                        Key = UniqueKey(field.Name + "_" + i),
                        BaseName = baseName,
                        FieldType = elementType,
                        Field = field,
                        IsArray = true,
                        ArrayIndex = i,
                        IsId = isId && i == 0,
                        DbType = columnDef.type ?? "int",
                        Verified = columnDef.verified,
                        Comment = columnDef.comment ?? fieldDef.comment,
                        ForeignTable = columnDef.foreignTable,
                        ForeignColumn = columnDef.foreignColumn
                    });
                }
            }
            else
            {
                columns.Add(new DbColumn
                {
                    Key = UniqueKey(field.Name),
                    BaseName = baseName,
                    FieldType = elementType,
                    Field = field,
                    IsId = isId,
                    DbType = columnDef.type ?? "int",
                    Verified = columnDef.verified,
                    Comment = columnDef.comment ?? fieldDef.comment,
                    ForeignTable = columnDef.foreignTable,
                    ForeignColumn = columnDef.foreignColumn
                });
            }
        }

        return columns;
    }

    public DataTable CreateDataTable()
    {
        if (wdbc != null)
            return wdbc.CreateDataTable(TableName);

        var table = new DataTable(TableName) { Locale = CultureInfo.InvariantCulture };
        foreach (var column in Columns)
            table.Columns.Add(column.Key, column.FieldType);

        if (IdColumn != null)
            table.PrimaryKey = new[] { table.Columns[IdColumn.Key] };

        table.BeginLoadData();
        foreach (DictionaryEntry entry in storage)
        {
            var row = table.NewRow();
            foreach (var column in Columns)
            {
                var value = GetColumnValue(entry.Value, column);
                row[column.Key] = value ?? DBNull.Value;
            }
            table.Rows.Add(row);
        }
        table.EndLoadData();
        table.AcceptChanges();
        return table;
    }

    private object GetColumnValue(object record, DbColumn column)
    {
        var value = column.Field.GetValue(record);
        if (column.IsArray)
            return value is Array array ? array.GetValue(column.ArrayIndex) : null;
        return value;
    }

    public void Save(DataTable table, string path)
    {
        if (wdbc != null)
        {
            wdbc.Save(table, path);
            FilePath = path;
            return;
        }

        storage.Clear();
        foreach (DataRow dataRow in table.Rows)
        {
            if (dataRow.RowState == DataRowState.Deleted)
                continue;

            var record = NewRecord();
            foreach (var column in Columns)
                SetColumnValue(record, column, dataRow[column.Key]);

            var id = IdColumn != null
                ? Convert.ToInt32(dataRow[IdColumn.Key], CultureInfo.InvariantCulture)
                : Convert.ToInt32(recordFields[idFieldIndex].GetValue(record), CultureInfo.InvariantCulture);

            if (storage.Contains(id))
                throw new InvalidDataException($"ID 重复：{id}");
            storage.Add(id, record);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        var tempPath = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var output = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                writeRecordsMethod.Invoke(parser, new object[] { storage, output });

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
        table.AcceptChanges();
    }

    private object NewRecord()
    {
        var record = Activator.CreateInstance(recordType);
        foreach (var field in recordFields)
        {
            if (field.FieldType.IsArray)
            {
                var elementType = field.FieldType.GetElementType();
                var cardinality = field.GetCustomAttribute<CardinalityAttribute>()?.Count ?? 1;
                var array = Array.CreateInstance(elementType, cardinality);
                if (elementType == typeof(string))
                    for (var i = 0; i < cardinality; i++)
                        array.SetValue(string.Empty, i);
                field.SetValue(record, array);
            }
            else if (field.FieldType == typeof(string))
            {
                field.SetValue(record, string.Empty);
            }
        }
        return record;
    }

    private static void SetColumnValue(object record, DbColumn column, object value)
    {
        var converted = ConvertValue(value, column.FieldType);
        if (column.IsArray)
        {
            var array = (Array)column.Field.GetValue(record);
            array.SetValue(converted, column.ArrayIndex);
        }
        else
        {
            column.Field.SetValue(record, converted);
        }
    }

    public static object ConvertValue(object value, Type targetType)
    {
        if (value == null || value is DBNull)
            return targetType == typeof(string) ? string.Empty : Activator.CreateInstance(targetType);
        if (targetType.IsInstanceOfType(value))
            return value;
        if (targetType == typeof(string))
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        parser?.ClearCache();
    }
}