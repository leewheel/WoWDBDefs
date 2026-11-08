using System.Data;
using System.Globalization;
using System.Text;

namespace WowDBEditor.Core.MySql;

/// <summary>
/// SQL 导出选项。<see cref="IncludeDrop"/> 为 true 时是「完整重建」，否则是「幂等增量」：
/// 只建表（IF NOT EXISTS）+ UPSERT 数据，脚本可以重复执行而结果不变。
/// </summary>
public sealed record SqlDumpOptions
{
    public bool IncludeDrop { get; init; } = true;
    public bool Upsert { get; init; }
    public bool CreateIfNotExists { get; init; }
    public int RowsPerInsert { get; init; } = 200;

    public static SqlDumpOptions Rebuild() => new() { IncludeDrop = true, Upsert = false };

    public static SqlDumpOptions Idempotent() => new()
    {
        IncludeDrop = false,
        Upsert = true,
        CreateIfNotExists = true
    };
}

public static class SqlDump
{
    public static void Write(DbTable table, DataTable data, string path, string targetTable, bool includeDrop,
        IProgress<TransferProgress> progress = null, CancellationToken ct = default)
        => Write(table, data, path, targetTable,
            includeDrop ? SqlDumpOptions.Rebuild() : SqlDumpOptions.Idempotent(), progress, ct);

    public static void Write(DbTable table, DataTable data, string path, string targetTable, SqlDumpOptions options,
        IProgress<TransferProgress> progress = null, CancellationToken ct = default)
    {
        options ??= SqlDumpOptions.Rebuild();
        var columns = table.Columns;
        var rows = data.AsEnumerable().Where(r => r.RowState != DataRowState.Deleted).ToList();
        var rowsPerInsert = Math.Max(1, options.RowsPerInsert);

        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("-- WowDBEditor SQL dump");
        writer.WriteLine("-- Source: " + table.FilePath);
        writer.WriteLine($"-- Table: {targetTable}  Rows: {rows.Count}  Mode: {(options.IncludeDrop ? "rebuild" : "idempotent upsert")}");
        writer.WriteLine("-- 该脚本可重复执行，结果一致。");
        writer.WriteLine("SET NAMES utf8mb4;");
        writer.WriteLine("SET FOREIGN_KEY_CHECKS=0;");
        writer.WriteLine();

        if (options.IncludeDrop)
            writer.WriteLine($"DROP TABLE IF EXISTS {MySqlService.QuoteIdentifier(targetTable)};");

        writer.Write($"CREATE TABLE {(options.CreateIfNotExists ? "IF NOT EXISTS " : "")}{MySqlService.QuoteIdentifier(targetTable)} (");
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) writer.Write(", ");
            writer.Write($"{MySqlService.QuoteIdentifier(columns[i].Key)} {MySqlService.MySqlTypeFor(columns[i].FieldType)} NOT NULL");
        }
        if (table.IdColumn != null)
            writer.Write($", PRIMARY KEY ({MySqlService.QuoteIdentifier(table.IdColumn.Key)})");
        writer.WriteLine(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
        writer.WriteLine();

        var columnList = string.Join(", ", columns.Select(c => MySqlService.QuoteIdentifier(c.Key)));
        var upsertClause = options.Upsert ? BuildUpsertClause(table) : "";
        var done = 0;
        for (var offset = 0; offset < rows.Count; offset += rowsPerInsert)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(rowsPerInsert, rows.Count - offset);
            writer.Write($"INSERT INTO {MySqlService.QuoteIdentifier(targetTable)} ({columnList}) VALUES ");
            for (var r = 0; r < count; r++)
            {
                if (r > 0) writer.Write(", ");
                writer.Write('(');
                for (var c = 0; c < columns.Count; c++)
                {
                    if (c > 0) writer.Write(", ");
                    writer.Write(FormatLiteral(rows[offset + r][columns[c].Key]));
                }
                writer.Write(')');
            }
            writer.Write(upsertClause);
            writer.WriteLine(";");
            done += count;
            progress?.Report(new TransferProgress(done, rows.Count));
        }

        writer.WriteLine();
        writer.WriteLine("SET FOREIGN_KEY_CHECKS=1;");
    }

    /// <summary>ON DUPLICATE KEY UPDATE 子句（主键列不参与更新）。</summary>
    private static string BuildUpsertClause(DbTable table)
    {
        var updatable = table.Columns
            .Where(c => table.IdColumn == null || c.Key != table.IdColumn.Key)
            .Select(c => $"{MySqlService.QuoteIdentifier(c.Key)}=VALUES({MySqlService.QuoteIdentifier(c.Key)})")
            .ToList();
        return updatable.Count == 0 ? "" : " ON DUPLICATE KEY UPDATE " + string.Join(", ", updatable);
    }

    private static string FormatLiteral(object value)
    {
        if (value == null || value is DBNull)
            return "NULL";
        return value switch
        {
            string s => "'" + EscapeString(s) + "'",
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static string EscapeString(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\0': sb.Append("\\0"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '"': sb.Append("\\\""); break;
                case '\x1a': sb.Append("\\Z"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}
