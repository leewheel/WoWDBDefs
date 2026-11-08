using System.Data;
using System.Globalization;
using System.Text;

namespace WowDBEditor.Core.MySql;

public static class SqlDump
{
    public static void Write(DbTable table, DataTable data, string path, string targetTable, bool includeDrop,
        IProgress<TransferProgress> progress = null, CancellationToken ct = default)
    {
        var columns = table.Columns;
        var rows = data.AsEnumerable().Where(r => r.RowState != DataRowState.Deleted).ToList();
        const int rowsPerInsert = 200;

        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("-- WowDBEditor SQL dump");
        writer.WriteLine("-- Source: " + table.FilePath);
        writer.WriteLine("SET NAMES utf8mb4;");
        writer.WriteLine("SET FOREIGN_KEY_CHECKS=0;");
        writer.WriteLine();

        if (includeDrop)
            writer.WriteLine($"DROP TABLE IF EXISTS {MySqlService.QuoteIdentifier(targetTable)};");

        writer.Write($"CREATE TABLE {MySqlService.QuoteIdentifier(targetTable)} (");
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
            writer.WriteLine(";");
            done += count;
            progress?.Report(new TransferProgress(done, rows.Count));
        }

        writer.WriteLine();
        writer.WriteLine("SET FOREIGN_KEY_CHECKS=1;");
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