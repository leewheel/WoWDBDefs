using System.Data;
using System.Globalization;
using System.Text;

namespace WowDBEditor.Core;

public sealed record CsvImportResult(int Inserted, int Updated, int Skipped, IReadOnlyList<string> Warnings);

/// <summary>RFC 4180 风格的 CSV 读写，用于和 Excel 之间批量搬运数据。</summary>
public static class CsvIO
{
    public static void Export(DbTable table, DataTable data, string path, bool includeHeader = true)
    {
        var columns = table.Columns;
        // Excel 需要 BOM 才能正确识别 UTF-8 中文
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));

        if (includeHeader)
            writer.WriteLine(string.Join(",", columns.Select(c => Escape(c.Key))));

        var line = new StringBuilder(256);
        foreach (DataRow row in data.Rows)
        {
            if (row.RowState == DataRowState.Deleted)
                continue;
            line.Clear();
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0) line.Append(',');
                line.Append(Escape(FormatValue(row[columns[i].Key], columns[i].FieldType)));
            }
            writer.WriteLine(line.ToString());
        }
    }

    public static CsvImportResult Import(DbTable table, DataTable data, string path, bool replaceAll)
    {
        var records = ReadRecords(path);
        if (records.Count == 0)
            throw new InvalidDataException("CSV 文件为空。");

        var header = records[0];
        var columns = table.Columns;
        var byName = new Dictionary<string, DbColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
            byName[column.Key] = column;

        // 表头 → 列索引
        var mapping = new List<(int Index, DbColumn Column)>();
        var warnings = new List<string>();
        for (var i = 0; i < header.Length; i++)
        {
            var name = header[i].Trim().Trim('\uFEFF');
            if (name.Length == 0)
                continue;
            if (byName.TryGetValue(name, out var column))
                mapping.Add((i, column));
            else
                warnings.Add("忽略未知列：" + name);
        }

        if (mapping.Count == 0)
            throw new InvalidDataException("CSV 表头没有匹配到任何字段，请确认表头使用 DBD 字段名（区分数组下标，如 Name_0）。");

        var idColumn = table.IdColumn;
        var idMapping = idColumn != null ? mapping.FirstOrDefault(m => m.Column.Key == idColumn.Key) : default;
        var hasId = idColumn != null && idMapping.Column != null;

        if (replaceAll)
            data.Rows.Clear();

        // 同一个 ID 可能对应多行（例如 CharBaseInfo 以 RaceID 为键，一个种族有多条），
        // 所以按"第几次出现"排队匹配，而不是用字典直接覆盖，否则会丢掉重复行。
        var existing = new Dictionary<int, Queue<DataRow>>();
        if (hasId)
        {
            foreach (DataRow row in data.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;
                var key = Convert.ToInt32(row[idColumn.Key], CultureInfo.InvariantCulture);
                if (!existing.TryGetValue(key, out var queue))
                    existing[key] = queue = new Queue<DataRow>();
                queue.Enqueue(row);
            }
        }

        var inserted = 0;
        var updated = 0;
        var skipped = 0;

        for (var r = 1; r < records.Count; r++)
        {
            var record = records[r];
            if (record.Length == 1 && record[0].Length == 0)
                continue; // 空行

            int id = 0;
            if (hasId)
            {
                var raw = idMapping.Index < record.Length ? record[idMapping.Index].Trim() : "";
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                {
                    skipped++;
                    if (warnings.Count < 12)
                        warnings.Add($"第 {r + 1} 行 ID 无效（[{raw}]），已跳过");
                    continue;
                }
            }

            DataRow target;
            if (hasId && existing.TryGetValue(id, out var found) && found.Count > 0)
            {
                target = found.Dequeue();
                updated++;
            }
            else
            {
                target = data.NewRow();
                foreach (var column in columns)
                    target[column.Key] = column.FieldType == typeof(string)
                        ? string.Empty
                        : Activator.CreateInstance(column.FieldType);
                if (hasId)
                {
                    // 新插入的行不要再进队列：CSV 里同一个 ID 出现多次时，
                    // 后面的行应该各自新增，而不是互相覆盖。
                    target[idColumn.Key] = id;
                }
                data.Rows.Add(target);
                inserted++;
            }

            foreach (var (index, column) in mapping)
            {
                if (column.IsId && hasId)
                    continue; // ID 已处理
                var raw = index < record.Length ? record[index] : "";
                try
                {
                    target[column.Key] = ParseValue(raw, column.FieldType);
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
                {
                    skipped++;
                    if (warnings.Count < 12)
                        warnings.Add($"第 {r + 1} 行 {column.Key} 解析失败（[{raw}]），已用默认值");
                    target[column.Key] = column.FieldType == typeof(string)
                        ? string.Empty
                        : Activator.CreateInstance(column.FieldType);
                }
            }
        }

        return new CsvImportResult(inserted, updated, skipped, warnings);
    }

    private static object ParseValue(string raw, Type type)
    {
        if (type == typeof(string))
            return raw ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return Activator.CreateInstance(type);
        return Convert.ChangeType(raw.Trim(), type, CultureInfo.InvariantCulture);
    }

    private static string FormatValue(object value, Type type)
    {
        if (value == null || value is DBNull)
            return string.Empty;
        return value switch
        {
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static string Escape(string value)
    {
        if (value == null)
            return string.Empty;
        var needsQuote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                         || (value.Length > 0 && (value[0] == ' ' || value[^1] == ' '));
        if (!needsQuote)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>解析成「记录 → 字段」列表，支持引号内的逗号与换行。</summary>
    private static List<string[]> ReadRecords(string path)
    {
        var text = File.ReadAllText(path, DetectEncoding(path));
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        void EndRecord()
        {
            EndField();
            // 跳过纯空行
            if (fields.Count > 1 || fields[0].Length > 0)
                records.Add(fields.ToArray());
            fields.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    EndField();
                    break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    EndRecord();
                    break;
                case '\n':
                    EndRecord();
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
            EndRecord();

        return records;
    }

    private static Encoding DetectEncoding(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[3];
        var read = stream.Read(head);
        if (read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            return Encoding.UTF8;
        if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            return Encoding.Unicode;
        if (read >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            return Encoding.BigEndianUnicode;
        // 无 BOM 时优先 UTF-8，非法序列会用替换字符而不是抛异常
        return new UTF8Encoding(false, false);
    }
}
