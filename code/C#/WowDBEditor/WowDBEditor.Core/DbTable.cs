using System.Collections;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text;
using DBCD;
using DBCD.IO;
using DBCD.IO.Attributes;
using DBDefsLib;
using DBDefsLib.Structs;

namespace WowDBEditor.Core;

public sealed class DbTable : IDisposable
{
    private readonly DBParser parser;
    private readonly Type recordType;
    private readonly IDictionary storage;
    private readonly MethodInfo writeRecordsMethod;
    private readonly FieldInfo[] recordFields;
    private readonly int idFieldIndex;

    public string FilePath { get; private set; }
    public string TableName { get; }
    public string Format => parser.Identifier;
    public uint LayoutHash => parser.LayoutHash;
    public uint TableHash => parser.TableHash;
    public DBDefinition Definition { get; }
    public VersionDefinitions Version { get; }
    public string BuildString { get; }
    public IReadOnlyList<DbColumn> Columns { get; }
    public DbColumn IdColumn { get; }
    public int RowCount => storage.Count;

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

        Columns = BuildColumns(version);

        var idField = recordFields.FirstOrDefault(f => f.IsDefined(typeof(IndexAttribute)))
                      ?? recordFields.FirstOrDefault(f => f.Name == "ID")
                      ?? recordFields.FirstOrDefault(f => f.FieldType == typeof(int));
        IdColumn = idField != null ? Columns.FirstOrDefault(c => c.Field == idField && c.ArrayIndex <= 0) : null;
        idFieldIndex = idField != null ? Array.IndexOf(recordFields, idField) : 0;
    }

    public static DbTable Open(string filePath, DefinitionService definitions, string build = null)
    {
        var tableName = Path.GetFileNameWithoutExtension(filePath);
        var definition = definitions.GetDefinition(tableName);

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var parser = new DBParser(stream);

        var version = ResolveVersion(definition, parser, tableName, build, out var effectiveBuild);

        var builder = new DBCDBuilder();
        var (recordType, _) = builder.Build(parser, definition, SanitizeTypeName(tableName), effectiveBuild);

        var getRecords = typeof(DBParser).GetMethod(nameof(DBParser.GetRecords), Type.EmptyTypes)
            .MakeGenericMethod(recordType);
        var storage = (IDictionary)getRecords.Invoke(parser, null);

        return new DbTable(filePath, tableName, parser, recordType, storage, definition, version, effectiveBuild);
    }

    /// <summary>
    /// 选版本：显式 BUILD → LayoutHash → 定义里只有一个版本。
    /// 老格式（WDBC/WDB2，没有 LayoutHash）无法自动判断时抛 <see cref="DefinitionNotFoundException"/>，
    /// 由界面弹出「选择定义版本」对话框，而不是直接报底层错误。
    /// </summary>
    private static VersionDefinitions ResolveVersion(DBDefinition definition, DBParser parser, string tableName,
        string build, out string effectiveBuild)
    {
        effectiveBuild = build;
        VersionDefinitions? version = null;

        if (!string.IsNullOrWhiteSpace(build))
            Utils.GetVersionDefinitionByBuild(definition, new Build(build), out version);

        if (version == null && parser.LayoutHash != 0)
            Utils.GetVersionDefinitionByLayoutHash(definition, parser.LayoutHash.ToString("X8"), out version);

        if (version == null && string.IsNullOrWhiteSpace(build) && definition.versionDefinitions is { Length: 1 })
            version = definition.versionDefinitions[0];

        if (version == null)
            throw new DefinitionNotFoundException(tableName, true, "无法根据文件头判断定义版本，请手动选择客户端版本。");

        var resolved = version.Value;
        if (string.IsNullOrWhiteSpace(effectiveBuild))
        {
            if (resolved.builds is { Length: > 0 })
                effectiveBuild = resolved.builds[0].ToString();
            else if (resolved.buildRanges is { Length: > 0 })
                effectiveBuild = resolved.buildRanges[0].minBuild.ToString();
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
        parser.ClearCache();
    }
}