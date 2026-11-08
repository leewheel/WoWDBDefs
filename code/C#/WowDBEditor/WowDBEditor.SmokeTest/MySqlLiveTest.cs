using System.Data;
using System.Globalization;
using System.Text;
using MySqlConnector;
using WowDBEditor.Core;
using WowDBEditor.Core.MySql;

namespace WowDBEditor.SmokeTest;

internal static class MySqlLiveTest
{
    private const string SamplePath = @"G:\COA-RECHECK-PACK\COA-PUBLISH-REPACK\Server\ClientData\dbc\Achievement.dbc";

    public static async Task<int> RunAsync(DefinitionService definitions)
    {
        Console.WriteLine("=== MySQL live test (127.0.0.1:3307) ===");
        var info = new MySqlConnectionInfo("127.0.0.1", 3307, "root", "", "wowdbeditor_test");

        await MySqlService.TestConnectionAsync(info);
        Console.WriteLine("  connection OK");

        using var table = DbTable.Open(SamplePath, definitions, "3.3.5.12340");
        var data = table.CreateDataTable();
        var titleColumn = table.Columns.First(c => c.Key == "Title_lang_0");
        var markerId = Convert.ToInt32(data.Rows[0][table.IdColumn.Key], CultureInfo.InvariantCulture);
        data.Rows[0][titleColumn.Key] = "小怡君MySQL测试_'\"\\";
        Console.WriteLine($"  sample loaded: {data.Rows.Count} rows, marker ID={markerId}");

        await MySqlService.ExportAsync(table, data, info, "Achievement", replaceExisting: true);
        Console.WriteLine("  export OK");

        await using (var connection = new MySqlConnection(MySqlService.BuildConnectionString(info)))
        {
            await connection.OpenAsync();
            await using var command = new MySqlCommand("SELECT COUNT(*) FROM Achievement", connection);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            if (count != data.Rows.Count)
                throw new Exception($"导出行数不一致: {count} vs {data.Rows.Count}");
            command.CommandText = $"SELECT Title_lang_0 FROM Achievement WHERE ID = {markerId}";
            var marker = (string)await command.ExecuteScalarAsync();
            if (marker != "小怡君MySQL测试_'\"\\")
                throw new Exception($"中文/转义字符回读不一致: [{marker}]");
        }
        Console.WriteLine("  row count + utf8mb4 marker OK");

        var fresh = table.CreateDataTable();
        var imported = await MySqlService.ImportAsync(fresh, table, info, "Achievement");
        if (imported != data.Rows.Count)
            throw new Exception($"导入行数不一致: {imported} vs {data.Rows.Count}");
        CompareAll(table, data, fresh);
        Console.WriteLine($"  import round-trip OK ({imported} rows)");

        try
        {
            await MySqlService.ExportAsync(table, data, info, "Achievement", replaceExisting: false);
            throw new Exception("应当抛出“表已存在”异常但未抛出");
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("  replace guard OK");
        }

        var dumpPath = Path.Combine(Path.GetTempPath(), "WowDBEditorSmoke", "Achievement_live.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(dumpPath));
        SqlDump.Write(table, data, dumpPath, "Achievement", includeDrop: true);
        await using (var connection = new MySqlConnection(MySqlService.BuildConnectionString(info, includeDatabase: false)))
        {
            await connection.OpenAsync();
            await using (var create = new MySqlCommand("CREATE DATABASE IF NOT EXISTS wowdbeditor_dump CHARACTER SET utf8mb4", connection))
                await create.ExecuteNonQueryAsync();
            await connection.ChangeDatabaseAsync("wowdbeditor_dump");
            foreach (var statement in SplitStatements(await File.ReadAllTextAsync(dumpPath)))
            {
                await using var statementCommand = new MySqlCommand(statement, connection);
                await statementCommand.ExecuteNonQueryAsync();
            }
            await using var command = new MySqlCommand("SELECT COUNT(*) FROM Achievement", connection);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            if (count != data.Rows.Count)
                throw new Exception($"SQL dump 回灌行数不一致: {count} vs {data.Rows.Count}");
            command.CommandText = $"SELECT Title_lang_0 FROM Achievement WHERE ID = {markerId}";
            var marker = (string)await command.ExecuteScalarAsync();
            if (marker != "小怡君MySQL测试_'\"\\")
                throw new Exception($"SQL dump 中文/转义回读不一致: [{marker}]");
        }
        Console.WriteLine("  sql dump replay OK");

        await using (var connection = new MySqlConnection(MySqlService.BuildConnectionString(info, includeDatabase: false)))
        {
            await connection.OpenAsync();
            foreach (var db in new[] { "wowdbeditor_test", "wowdbeditor_dump" })
            {
                await using var drop = new MySqlCommand($"DROP DATABASE IF EXISTS `{db}`", connection);
                await drop.ExecuteNonQueryAsync();
            }
        }
        Console.WriteLine("  cleanup OK");
        Console.WriteLine("MYSQL ALL PASS");
        return 0;
    }

    private static IEnumerable<string> SplitStatements(string sql)
    {
        var sb = new StringBuilder();
        var inString = false;
        var escaped = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            if (!inString && ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (inString)
            {
                sb.Append(ch);
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '\'') inString = false;
                continue;
            }
            if (ch == '\'')
            {
                inString = true;
                sb.Append(ch);
                continue;
            }
            if (ch == ';')
            {
                var statement = sb.ToString().Trim();
                if (statement.Length > 0)
                    yield return statement;
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        var tail = sb.ToString().Trim();
        if (tail.Length > 0)
            yield return tail;
    }

    private static void CompareAll(DbTable table, DataTable expected, DataTable actual)
    {
        if (expected.Rows.Count != actual.Rows.Count)
            throw new Exception($"行数不一致: {expected.Rows.Count} vs {actual.Rows.Count}");
        var index = actual.AsEnumerable().ToDictionary(r => r[table.IdColumn.Key]);
        foreach (DataRow row in expected.Rows)
        {
            var other = index[row[table.IdColumn.Key]];
            foreach (var column in table.Columns)
            {
                var a = row[column.Key];
                var b = other[column.Key];
                if (!Equals(a, b))
                    throw new Exception($"单元格不一致 ID={row[table.IdColumn.Key]} col={column.Key}: [{a}] vs [{b}]");
            }
        }
    }
}