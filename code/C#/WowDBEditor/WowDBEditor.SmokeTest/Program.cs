using System.Data;
using System.Globalization;
using WowDBEditor.Core;
using WowDBEditor.Core.MySql;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

var definitionsDir = DefinitionService.FindDefinitionsDirectory(AppContext.BaseDirectory)
                     ?? @"G:\WoWTools\DBReader\WoWDBDefs\definitions";
Console.WriteLine($"Definitions: {definitionsDir}");
var definitions = new DefinitionService(definitionsDir);

if (args.Contains("--mysql"))
    return await WowDBEditor.SmokeTest.MySqlLiveTest.RunAsync(definitions);

var workDir = Path.Combine(Path.GetTempPath(), "WowDBEditorSmoke");
Directory.CreateDirectory(workDir);

var cases = new (string Name, string Path, string Build)[]
{
    ("WDBC Achievement (3.3.5)", @"G:\COA-RECHECK-PACK\COA-PUBLISH-REPACK\Server\ClientData\dbc\Achievement.dbc", "3.3.5.12340"),
    ("WDC1 Map (7.3.5 zhCN)", @"G:\MYWOW\MYWOW735\data\dbc\zhcn\Map.db2", null),
    ("WDC5 Map (zhCN sniff)", @"G:\1.Classic_Titan\SniffTools\LWcoreSniffTools\dbc\zhCN\Map.db2", null),
};

var failures = 0;
foreach (var testCase in cases)
{
    Console.WriteLine();
    Console.WriteLine($"=== {testCase.Name} ===");
    try
    {
        if (!File.Exists(testCase.Path))
        {
            Console.WriteLine("SKIP: 样本文件不存在 " + testCase.Path);
            continue;
        }
        RunCase(testCase, definitions, workDir);
        Console.WriteLine("PASS");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine("FAIL: " + ex);
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures;

static void RunCase((string Name, string Path, string Build) testCase, DefinitionService definitions, string workDir)
{
    using var table = DbTable.Open(testCase.Path, definitions, testCase.Build);
    Console.WriteLine($"  format={table.Format} layout={table.LayoutHash:X8} rows={table.RowCount} cols={table.Columns.Count} id={table.IdColumn?.Key ?? "(none)"}");
    Console.WriteLine($"  version comment: {table.Version.comment}");

    var data = table.CreateDataTable();
    if (data.Rows.Count != table.RowCount)
        throw new Exception($"行数不一致: DataTable={data.Rows.Count} storage={table.RowCount}");

    var savedPath = Path.Combine(workDir, Path.GetFileName(testCase.Path));
    table.Save(data, savedPath);

    using var reopened = DbTable.Open(savedPath, definitions, testCase.Build);
    var data2 = reopened.CreateDataTable();
    CompareTables(table, data, reopened, data2);
    Console.WriteLine($"  round-trip OK ({data.Rows.Count} rows x {data.Columns.Count} cols)");

    var editColumn = table.Columns.FirstOrDefault(c => !c.IsId && c.FieldType == typeof(string))
                     ?? table.Columns.FirstOrDefault(c => !c.IsId && c.FieldType != typeof(string));
    if (editColumn != null && data.Rows.Count > 0)
    {
        var original = data.Rows[0][editColumn.Key];
        object newValue = editColumn.FieldType == typeof(string)
            ? "小怡君测试_" + Guid.NewGuid().ToString("N")[..8]
            : DbTable.ConvertValue(42, editColumn.FieldType);
        data.Rows[0][editColumn.Key] = newValue;
        table.Save(data, savedPath);

        using var edited = DbTable.Open(savedPath, definitions, testCase.Build);
        var editedData = edited.CreateDataTable();
        var id = data.Rows[0][table.IdColumn.Key];
        var found = editedData.AsEnumerable().First(r => Equals(r[table.IdColumn.Key], id));
        var actual = found[editColumn.Key];
        if (!Equals(Convert.ToString(actual, CultureInfo.InvariantCulture), Convert.ToString(newValue, CultureInfo.InvariantCulture)))
            throw new Exception($"编辑回读不一致: 期望 {newValue} 实际 {actual}");
        Console.WriteLine($"  edit round-trip OK (col={editColumn.Key}, {original} -> {actual})");
    }

    var dumpPath = Path.Combine(workDir, table.TableName + ".sql");
    SqlDump.Write(table, data, dumpPath, table.TableName, includeDrop: true);
    var dumpText = File.ReadAllText(dumpPath);
    if (!dumpText.Contains("CREATE TABLE") || !dumpText.Contains("SET NAMES utf8mb4"))
        throw new Exception("SQL dump 内容异常");
    if (data.Rows.Count > 0 && !dumpText.Contains("INSERT INTO"))
        throw new Exception("SQL dump 缺少 INSERT");
    Console.WriteLine($"  sql dump OK ({new FileInfo(dumpPath).Length} bytes)");
}

static void CompareTables(DbTable t1, DataTable d1, DbTable t2, DataTable d2)
{
    if (d1.Columns.Count != d2.Columns.Count)
        throw new Exception($"列数不一致: {d1.Columns.Count} vs {d2.Columns.Count}");
    for (var i = 0; i < d1.Columns.Count; i++)
        if (d1.Columns[i].ColumnName != d2.Columns[i].ColumnName || d1.Columns[i].DataType != d2.Columns[i].DataType)
            throw new Exception($"列不一致 @{i}: {d1.Columns[i].ColumnName}:{d1.Columns[i].DataType} vs {d2.Columns[i].ColumnName}:{d2.Columns[i].DataType}");
    if (d1.Rows.Count != d2.Rows.Count)
        throw new Exception($"行数不一致: {d1.Rows.Count} vs {d2.Rows.Count}");

    var index = new Dictionary<object, DataRow>();
    foreach (DataRow row in d2.Rows)
        index[row[t2.IdColumn.Key]] = row;

    foreach (DataRow row in d1.Rows)
    {
        var other = index[row[t1.IdColumn.Key]];
        foreach (var column in t1.Columns)
        {
            var a = row[column.Key];
            var b = other[column.Key];
            if (!Equals(a, b))
                throw new Exception($"单元格不一致 ID={row[t1.IdColumn.Key]} col={column.Key}: [{a}] vs [{b}]");
        }
    }
}