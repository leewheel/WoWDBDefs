using System.Data;
using System.Globalization;
using WowDBEditor.Core;
using WowDBEditor.Core.MySql;
using WowDBEditor.Core.Sql;

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

    RunCsvCase(testCase, definitions, workDir, table, data);
    RunUndoCase(testCase, definitions, table, data);
    RunSqlScriptCase(testCase, definitions, workDir, table, data);

static void RunSqlScriptCase((string Name, string Path, string Build) testCase, DefinitionService definitions,
    string workDir, DbTable table, DataTable data)
{
    var idempotentPath = Path.Combine(workDir, table.TableName + ".idempotent.sql");
    SqlDump.Write(table, data, idempotentPath, table.TableName, SqlDumpOptions.Idempotent());
    var text = File.ReadAllText(idempotentPath);
    if (!text.Contains("CREATE TABLE IF NOT EXISTS") || !text.Contains("ON DUPLICATE KEY UPDATE"))
        throw new Exception("幂等 SQL 缺少 IF NOT EXISTS / ON DUPLICATE KEY UPDATE");
    if (text.Contains("DROP TABLE IF EXISTS"))
        throw new Exception("幂等 SQL 不应包含 DROP TABLE");

    var rebuildPath = Path.Combine(workDir, table.TableName + ".rebuild.sql");
    SqlDump.Write(table, data, rebuildPath, table.TableName, SqlDumpOptions.Rebuild());
    if (!File.ReadAllText(rebuildPath).Contains("DROP TABLE IF EXISTS"))
        throw new Exception("完整重建 SQL 缺少 DROP TABLE IF EXISTS");

    var expectedRows = data.AsEnumerable().Count(r => r.RowState != DataRowState.Deleted);

    using var target = DbTable.Open(testCase.Path, definitions, testCase.Build);
    var targetData = target.CreateDataTable();
    var first = SqlScriptImporter.Apply(target, targetData, idempotentPath, replaceAll: true);
    if (targetData.Rows.Count != expectedRows)
        throw new Exception($"SQL 导入行数不一致: {targetData.Rows.Count} vs {expectedRows}");

    // 再导一次，验证幂等（不重复插入）
    var second = SqlScriptImporter.Apply(target, targetData, idempotentPath, replaceAll: false);
    if (targetData.Rows.Count != expectedRows)
        throw new Exception($"幂等重放后行数变化: {targetData.Rows.Count} vs {expectedRows}");
    if (second.Inserted != 0)
        throw new Exception($"幂等重放不应新增行，实际新增 {second.Inserted}");

    var index = new Dictionary<object, DataRow>();
    foreach (DataRow row in targetData.Rows)
        index[row[target.IdColumn.Key]] = row;
    foreach (DataRow row in data.Rows)
    {
        if (row.RowState == DataRowState.Deleted)
            continue;
        if (!index.TryGetValue(row[table.IdColumn.Key], out var other))
            throw new Exception($"SQL 导入丢失 ID={row[table.IdColumn.Key]}");
        foreach (var column in table.Columns)
        {
            var a = row[column.Key];
            var b = other[column.Key];
            if (column.FieldType == typeof(float))
            {
                if (Math.Abs((float)Convert.ChangeType(a, typeof(float)) - (float)Convert.ChangeType(b, typeof(float))) > 1e-6f)
                    throw new Exception($"SQL 浮点不一致 ID={row[table.IdColumn.Key]} col={column.Key}: [{a}] vs [{b}]");
            }
            else if (!Equals(a, b))
                throw new Exception($"SQL 单元格不一致 ID={row[table.IdColumn.Key]} col={column.Key}: [{a}] vs [{b}]");
        }
    }
    Console.WriteLine($"  sql script OK (idempotent insert={first.Inserted}, update={first.Updated}, replay=0/{second.Updated})");
}

static void RunCsvCase((string Name, string Path, string Build) testCase, DefinitionService definitions, string workDir,
    DbTable table, DataTable data)
{
    var csvPath = Path.Combine(workDir, table.TableName + ".csv");
    CsvIO.Export(table, data, csvPath);

    using var target = DbTable.Open(testCase.Path, definitions, testCase.Build);
    var targetData = target.CreateDataTable();
    var result = CsvIO.Import(target, targetData, csvPath, replaceAll: true);
    if (targetData.Rows.Count != data.Rows.Count)
        throw new Exception($"CSV 导入行数不一致: {targetData.Rows.Count} vs {data.Rows.Count}");

    var index = new Dictionary<object, DataRow>();
    foreach (DataRow row in targetData.Rows)
        index[row[target.IdColumn.Key]] = row;

    foreach (DataRow row in data.Rows)
    {
        if (row.RowState == DataRowState.Deleted)
            continue;
        if (!index.TryGetValue(row[table.IdColumn.Key], out var other))
            throw new Exception($"CSV 导入丢失 ID={row[table.IdColumn.Key]}");
        foreach (var column in table.Columns)
        {
            var a = row[column.Key];
            var b = other[column.Key];
            if (column.FieldType == typeof(float))
            {
                if (Math.Abs((float)Convert.ChangeType(a, typeof(float)) - (float)Convert.ChangeType(b, typeof(float))) > 1e-6f)
                    throw new Exception($"CSV 浮点不一致 ID={row[table.IdColumn.Key]} col={column.Key}: [{a}] vs [{b}]");
            }
            else if (!Equals(a, b))
                throw new Exception($"CSV 单元格不一致 ID={row[table.IdColumn.Key]} col={column.Key}: [{a}] vs [{b}]");
        }
    }
    Console.WriteLine($"  csv round-trip OK (inserted={result.Inserted}, skipped={result.Skipped})");
}

static void RunUndoCase((string Name, string Path, string Build) testCase, DefinitionService definitions, DbTable table, DataTable data)
{
    if (table.IdColumn == null || data.Rows.Count == 0)
        return;

    using var reopened = DbTable.Open(testCase.Path, definitions, testCase.Build);
    var work = reopened.CreateDataTable();
    using var undo = new TableUndo(work, reopened.IdColumn.Key);

    var column = reopened.Columns.First(c => !c.IsId && c.FieldType == typeof(string));
    var id = work.Rows[0][reopened.IdColumn.Key];
    var before = work.Rows[0][column.Key];
    work.Rows[0][column.Key] = "UNDO_TEST_VALUE";
    if (undo.UndoCount != 1) throw new Exception($"撤销栈记录数异常: {undo.UndoCount}");
    if (!undo.CanUndo) throw new Exception("撤销栈不可用");

    undo.Undo();
    if (!Equals(work.Rows[0][column.Key], before))
        throw new Exception($"撤销未恢复原值: [{work.Rows[0][column.Key]}] != [{before}]");
    undo.Redo();
    if (!Equals(work.Rows[0][column.Key], "UNDO_TEST_VALUE"))
        throw new Exception($"重做未生效: [{work.Rows[0][column.Key]}]");

    var rowCount = CountAlive(work);
    var deleteRow = work.Rows[0];
    deleteRow.Delete();
    undo.Undo();
    if (CountAlive(work) != rowCount)
        throw new Exception($"撤销删除后行数异常: {work.Rows.Count} vs {rowCount}");

    // 应用里删除是 Delete + Rows.Remove：验证不会重复入栈、撤销能恢复
    var removed = work.Rows[0];
    removed.Delete();
    work.Rows.Remove(removed);
    undo.Undo();
    if (CountAlive(work) != rowCount)
        throw new Exception($"Delete+Remove 撤销后行数异常: {CountAlive(work)} vs {rowCount}");

    var newRow = work.NewRow();
    foreach (var col in reopened.Columns)
        newRow[col.Key] = col.FieldType == typeof(string) ? "新增" : Activator.CreateInstance(col.FieldType);
    var maxId = work.AsEnumerable().Where(r => r.RowState != DataRowState.Deleted)
        .Select(r => Convert.ToInt32(r[reopened.IdColumn.Key], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
    newRow[reopened.IdColumn.Key] = maxId + 1;
    work.Rows.Add(newRow);
    undo.Undo();
    if (CountAlive(work) != rowCount)
        throw new Exception($"撤销新增后行数异常: {CountAlive(work)} vs {rowCount}");
    undo.Redo();
    if (CountAlive(work) != rowCount + 1)
        throw new Exception($"重做新增后行数异常: {CountAlive(work)} vs {rowCount + 1}");

    Console.WriteLine($"  undo/redo OK (id={id}, col={column.Key})");
}

static int CountAlive(DataTable table) =>
    table.Rows.Cast<DataRow>().Count(r => r.RowState != DataRowState.Deleted);
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