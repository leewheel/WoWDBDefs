using System.Data;
using System.Globalization;
using System.Text;
using MySqlConnector;

namespace WowDBEditor.Core.MySql;

public sealed record MySqlConnectionInfo(string Host, int Port, string User, string Password, string Database);

public readonly record struct TransferProgress(int Done, int Total);

public static class MySqlService
{
    public static string BuildConnectionString(MySqlConnectionInfo info, bool includeDatabase = true)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = info.Host,
            Port = (uint)info.Port,
            UserID = info.User,
            Password = info.Password,
            SslMode = MySqlSslMode.Preferred,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 600,
            AllowUserVariables = true,
            CharacterSet = "utf8mb4"
        };
        if (includeDatabase && !string.IsNullOrWhiteSpace(info.Database))
            builder.Database = info.Database;
        return builder.ConnectionString;
    }

    public static string QuoteIdentifier(string name) => "`" + name.Replace("`", "``") + "`";

    public static string MySqlTypeFor(Type clrType) => clrType switch
    {
        var t when t == typeof(sbyte) => "TINYINT",
        var t when t == typeof(byte) => "TINYINT UNSIGNED",
        var t when t == typeof(short) => "SMALLINT",
        var t when t == typeof(ushort) => "SMALLINT UNSIGNED",
        var t when t == typeof(int) => "INT",
        var t when t == typeof(uint) => "INT UNSIGNED",
        var t when t == typeof(long) => "BIGINT",
        var t when t == typeof(ulong) => "BIGINT UNSIGNED",
        var t when t == typeof(float) => "FLOAT",
        var t when t == typeof(double) => "DOUBLE",
        var t when t == typeof(string) => "TEXT",
        _ => throw new NotSupportedException("不支持的字段类型：" + clrType.Name)
    };

    public static async Task TestConnectionAsync(MySqlConnectionInfo info, CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(BuildConnectionString(info, includeDatabase: false));
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT VERSION()", connection);
        await command.ExecuteScalarAsync(ct);
    }

    public static async Task<List<string>> ListDatabasesAsync(MySqlConnectionInfo info, CancellationToken ct = default)
    {
        var result = new List<string>();
        await using var connection = new MySqlConnection(BuildConnectionString(info, includeDatabase: false));
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SHOW DATABASES", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    public static async Task<List<string>> ListTablesAsync(MySqlConnectionInfo info, CancellationToken ct = default)
    {
        var result = new List<string>();
        await using var connection = new MySqlConnection(BuildConnectionString(info));
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SHOW TABLES", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    public static async Task ExportAsync(DbTable table, DataTable data, MySqlConnectionInfo info, string targetTable,
        bool replaceExisting, IProgress<TransferProgress> progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetTable))
            throw new ArgumentException("目标表名不能为空。");

        var columns = table.Columns;
        var rows = data.AsEnumerable().Where(r => r.RowState != DataRowState.Deleted).ToList();

        await using var connection = new MySqlConnection(BuildConnectionString(info, includeDatabase: false));
        await connection.OpenAsync(ct);

        await using (var createDb = new MySqlCommand(
                         $"CREATE DATABASE IF NOT EXISTS {QuoteIdentifier(info.Database)} CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
                         connection))
            await createDb.ExecuteNonQueryAsync(ct);
        await connection.ChangeDatabaseAsync(info.Database, ct);

        var exists = await TableExistsAsync(connection, info.Database, targetTable, ct);
        if (exists)
        {
            if (!replaceExisting)
                throw new InvalidOperationException($"表 `{targetTable}` 已存在。勾选“替换已存在的表”后可覆盖。");
            await using var drop = new MySqlCommand($"DROP TABLE IF EXISTS {QuoteIdentifier(targetTable)}", connection);
            await drop.ExecuteNonQueryAsync(ct);
        }

        var createSql = new StringBuilder();
        createSql.Append($"CREATE TABLE {QuoteIdentifier(targetTable)} (");
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) createSql.Append(", ");
            createSql.Append($"{QuoteIdentifier(columns[i].Key)} {MySqlTypeFor(columns[i].FieldType)} NOT NULL");
        }
        if (table.IdColumn != null)
            createSql.Append($", PRIMARY KEY ({QuoteIdentifier(table.IdColumn.Key)})");
        createSql.Append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
        await using (var create = new MySqlCommand(createSql.ToString(), connection))
            await create.ExecuteNonQueryAsync(ct);

        var columnList = string.Join(", ", columns.Select(c => QuoteIdentifier(c.Key)));
        var batchSize = Math.Max(1, Math.Min(500, 60000 / Math.Max(1, columns.Count)));

        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            var done = 0;
            for (var offset = 0; offset < rows.Count; offset += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(batchSize, rows.Count - offset);
                var sql = new StringBuilder($"INSERT INTO {QuoteIdentifier(targetTable)} ({columnList}) VALUES ");
                await using var command = new MySqlCommand { Connection = connection, Transaction = transaction };

                for (var r = 0; r < count; r++)
                {
                    if (r > 0) sql.Append(", ");
                    sql.Append('(');
                    for (var c = 0; c < columns.Count; c++)
                    {
                        if (c > 0) sql.Append(", ");
                        var parameterName = $"@p{r}_{c}";
                        sql.Append(parameterName);
                        var value = rows[offset + r][columns[c].Key];
                        command.Parameters.AddWithValue(parameterName, value is DBNull ? null : value);
                    }
                    sql.Append(')');
                }

                command.CommandText = sql.ToString();
                await command.ExecuteNonQueryAsync(ct);
                done += count;
                progress?.Report(new TransferProgress(done, rows.Count));
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public static async Task<int> ImportAsync(DataTable data, DbTable table, MySqlConnectionInfo info, string sourceTable,
        IProgress<TransferProgress> progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceTable))
            throw new ArgumentException("来源表名不能为空。");

        await using var connection = new MySqlConnection(BuildConnectionString(info));
        await connection.OpenAsync(ct);

        var remoteColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var schemaCommand = new MySqlCommand(
                         "SELECT COLUMN_NAME, DATA_TYPE FROM information_schema.COLUMNS " +
                         "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table ORDER BY ORDINAL_POSITION", connection))
        {
            schemaCommand.Parameters.AddWithValue("@table", sourceTable);
            await using var reader = await schemaCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                remoteColumns[reader.GetString(0)] = reader.GetString(1);
        }

        if (remoteColumns.Count == 0)
            throw new InvalidOperationException($"数据库 `{info.Database}` 中不存在表 `{sourceTable}`。");

        var missing = table.Columns.Where(c => !remoteColumns.ContainsKey(c.Key)).Select(c => c.Key).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("MySQL 表缺少以下列：" + string.Join(", ", missing));

        var total = 0;
        await using (var countCommand = new MySqlCommand($"SELECT COUNT(*) FROM {QuoteIdentifier(sourceTable)}", connection))
            total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

        var selectList = string.Join(", ", table.Columns.Select(c => QuoteIdentifier(c.Key)));
        await using var selectCommand = new MySqlCommand($"SELECT {selectList} FROM {QuoteIdentifier(sourceTable)}", connection);
        await using var selectReader = await selectCommand.ExecuteReaderAsync(ct);

        var imported = new List<object[]>(total);
        var done = 0;
        while (await selectReader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var values = new object[table.Columns.Count];
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var raw = selectReader.GetValue(i);
                values[i] = DbTable.ConvertValue(raw, table.Columns[i].FieldType);
            }
            imported.Add(values);
            done++;
            if (done % 1000 == 0)
                progress?.Report(new TransferProgress(done, total));
        }
        await selectReader.CloseAsync();

        try
        {
            data.BeginLoadData();
            data.Rows.Clear();
            foreach (var values in imported)
            {
                var row = data.NewRow();
                for (var i = 0; i < table.Columns.Count; i++)
                    row[table.Columns[i].Key] = values[i];
                data.Rows.Add(row);
            }
            data.EndLoadData();
        }
        catch (ConstraintException ex)
        {
            throw new InvalidDataException("导入失败：MySQL 表中存在重复 ID。" + ex.Message, ex);
        }

        progress?.Report(new TransferProgress(imported.Count, imported.Count));
        return imported.Count;
    }

    private static async Task<bool> TableExistsAsync(MySqlConnection connection, string database, string tableName, CancellationToken ct)
    {
        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table", connection);
        command.Parameters.AddWithValue("@db", database);
        command.Parameters.AddWithValue("@table", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }
}