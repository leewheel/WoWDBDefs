using System.Data;
using System.Globalization;
using System.Text;

namespace WowDBEditor.Core.Sql;

/// <summary>
/// MySQL 导出的 .sql 文件解析器：只处理本工具关心的部分（CREATE TABLE 表名、INSERT/REPLACE 数据），
/// 其余语句（SET、注释、索引等）安全跳过。
/// </summary>
public static class SqlScriptReader
{
    public sealed record Block(string TableName, string[] Columns, List<object[]> Rows);

    public sealed record Script(string TableName, string[] Columns, List<object[]> Rows, int Statements);

    public static Script Read(string path)
    {
        var text = File.ReadAllText(path);
        var parser = new Parser(text);
        var blocks = parser.Parse();

        if (blocks.Count == 0)
            throw new InvalidDataException("SQL 文件中没有找到 INSERT / REPLACE 数据语句。");

        var columns = blocks[0].Columns;
        var table = blocks[0].TableName;
        var merged = new List<object[]>();

        foreach (var block in blocks)
        {
            if (block.Columns.Length != columns.Length
                || !block.Columns.SequenceEqual(columns, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(block.TableName, table, StringComparison.OrdinalIgnoreCase))
                    continue; // 忽略其他表的 INSERT
                throw new InvalidDataException(
                    $"SQL 文件中同一张表出现了不一致的列清单（{block.TableName}），请在导出时保持列固定。");
            }
            merged.AddRange(block.Rows);
        }

        return new Script(table, columns, merged, blocks.Count);
    }

    private sealed class Parser
    {
        private readonly string text;
        private int pos;

        public Parser(string text) => this.text = text;

        public List<Block> Parse()
        {
            var blocks = new List<Block>();
            while (pos < text.Length)
            {
                SkipTrivia();
                if (pos >= text.Length)
                    break;

                var start = pos;
                var word = ReadKeyword();
                if (word is "INSERT" or "REPLACE")
                {
                    var block = ParseInsert(word);
                    if (block != null)
                        blocks.Add(block);
                    continue;
                }

                if (word == null)
                {
                    pos = start + 1;
                    continue;
                }

                SkipStatement();
            }
            return blocks;
        }

        private Block ParseInsert(string keyword)
        {
            SkipTrivia();
            var next = PeekKeyword();
            if (next is "IGNORE" or "LOW_PRIORITY" or "DELAYED" or "HIGH_PRIORITY")
            {
                ReadKeyword();
                SkipTrivia();
                next = PeekKeyword();
            }
            if (next == "INTO")
            {
                ReadKeyword();
                SkipTrivia();
            }

            var table = ReadQualifiedName();
            if (string.IsNullOrEmpty(table))
            {
                SkipStatement();
                return null;
            }

            SkipTrivia();
            var columns = new List<string>();
            if (Peek() == '(')
            {
                pos++;
                while (true)
                {
                    SkipTrivia();
                    var name = ReadIdentifier();
                    if (name == null)
                        break;
                    columns.Add(name);
                    SkipTrivia();
                    if (Peek() == ',')
                    {
                        pos++;
                        continue;
                    }
                    if (Peek() == ')')
                        pos++;
                    break;
                }
            }

            SkipTrivia();
            next = PeekKeyword();
            if (next is not ("VALUES" or "VALUE"))
            {
                SkipStatement();
                return null;
            }
            ReadKeyword();

            var rows = new List<object[]>();
            while (true)
            {
                SkipTrivia();
                if (Peek() != '(')
                    break;
                pos++;
                var values = new List<object>();
                while (true)
                {
                    SkipTrivia();
                    values.Add(ReadValue());
                    SkipTrivia();
                    if (Peek() == ',')
                    {
                        pos++;
                        continue;
                    }
                    if (Peek() == ')')
                        pos++;
                    break;
                }
                rows.Add(values.ToArray());
                SkipTrivia();

                if (Peek() == ',')
                {
                    pos++;
                    continue;
                }
                break;
            }

            if (columns.Count == 0 || rows.Count == 0)
                return null;
            if (rows.Any(r => r.Length != columns.Count))
                throw new InvalidDataException($"SQL 中 VALUES 的字段数与列清单不一致（表 {table}）。");

            SkipStatement();
            return new Block(table, columns.ToArray(), rows);
        }

        private object ReadValue()
        {
            SkipTrivia();
            var ch = Peek();
            if (ch == '\'' || ch == '"')
                return ReadQuoted(ch);

            var word = PeekKeyword();
            if (word == "NULL")
            {
                ReadKeyword();
                return null;
            }
            if (word == "TRUE" || word == "FALSE")
            {
                ReadKeyword();
                return word == "TRUE" ? 1L : 0L;
            }

            if (ch == '0' && pos + 1 < text.Length && (text[pos + 1] == 'x' || text[pos + 1] == 'X'))
            {
                pos += 2;
                var hex = ReadWhile(c => Uri.IsHexDigit(c));
                return hex.Length == 0 ? 0L : long.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            var start = pos;
            while (pos < text.Length && Peek() is not (',' or ')'))
                pos++;
            var raw = text[start..pos].Trim();

            if (raw.Length == 0)
                return null;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                return l;
            if (ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ul))
                return ul;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
            return raw;
        }

        private string ReadQuoted(char quote)
        {
            pos++; // 起始引号
            var sb = new StringBuilder();
            while (pos < text.Length)
            {
                var ch = text[pos++];
                if (ch == quote)
                {
                    if (pos < text.Length && text[pos] == quote)
                    {
                        sb.Append(quote);
                        pos++;
                        continue;
                    }
                    break;
                }
                if (ch == '\\' && pos < text.Length)
                {
                    var esc = text[pos++];
                    sb.Append(esc switch
                    {
                        '0' => '\0',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        'b' => '\b',
                        'Z' => '\x1a',
                        _ => esc
                    });
                    continue;
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        private string ReadQualifiedName()
        {
            var first = ReadIdentifier();
            if (first == null)
                return null;
            SkipTrivia();
            if (Peek() == '.')
            {
                pos++;
                SkipTrivia();
                var second = ReadIdentifier();
                return second ?? first;
            }
            return first;
        }

        private string ReadIdentifier()
        {
            SkipTrivia();
            if (Peek() == '`')
            {
                pos++;
                var sb = new StringBuilder();
                while (pos < text.Length)
                {
                    var ch = text[pos++];
                    if (ch == '`')
                    {
                        if (pos < text.Length && text[pos] == '`')
                        {
                            sb.Append('`');
                            pos++;
                            continue;
                        }
                        break;
                    }
                    sb.Append(ch);
                }
                return sb.ToString();
            }

            var start = pos;
            ReadWhile(c => char.IsLetterOrDigit(c) || c is '_' or '$');
            return pos > start ? text[start..pos] : null;
        }

        private string ReadKeyword()
        {
            SkipTrivia();
            var start = pos;
            ReadWhile(c => char.IsLetter(c) || c is '_' or '$');
            if (pos == start)
                return null;
            return text[start..pos].ToUpperInvariant();
        }

        private string PeekKeyword()
        {
            var save = pos;
            var word = ReadKeyword();
            pos = save;
            return word;
        }

        private void SkipStatement()
        {
            while (pos < text.Length)
            {
                SkipTrivia();
                if (pos >= text.Length)
                    return;
                var ch = Peek();
                if (ch == ';')
                {
                    pos++;
                    return;
                }
                if (ch is '\'' or '"' or '`')
                {
                    SkipQuoted(ch);
                    continue;
                }
                pos++;
            }
        }

        private void SkipQuoted(char quote)
        {
            pos++;
            while (pos < text.Length)
            {
                var ch = text[pos++];
                if (ch == '\\' && pos < text.Length)
                {
                    pos++;
                    continue;
                }
                if (ch == quote)
                {
                    if (pos < text.Length && text[pos] == quote)
                    {
                        pos++;
                        continue;
                    }
                    return;
                }
            }
        }

        private void SkipTrivia()
        {
            while (pos < text.Length)
            {
                var ch = text[pos];
                if (char.IsWhiteSpace(ch))
                {
                    pos++;
                    continue;
                }
                if (ch == '-' && pos + 1 < text.Length && text[pos + 1] == '-')
                {
                    while (pos < text.Length && text[pos] != '\n')
                        pos++;
                    continue;
                }
                if (ch == '#')
                {
                    while (pos < text.Length && text[pos] != '\n')
                        pos++;
                    continue;
                }
                if (ch == '/' && pos + 1 < text.Length && text[pos + 1] == '*')
                {
                    pos += 2;
                    while (pos + 1 < text.Length && !(text[pos] == '*' && text[pos + 1] == '/'))
                        pos++;
                    pos = Math.Min(text.Length, pos + 2);
                    continue;
                }
                break;
            }
        }

        private char Peek() => pos < text.Length ? text[pos] : '\0';

        private string ReadWhile(Func<char, bool> predicate)
        {
            var start = pos;
            while (pos < text.Length && predicate(text[pos]))
                pos++;
            return text.Substring(start, pos - start);
        }
    }
}

/// <summary>把 .sql 文件里的数据按主键幂等地合并进当前表（已存在的行更新，不存在的新增）。</summary>
public static class SqlScriptImporter
{
    public sealed record Result(int Inserted, int Updated, int Skipped, IReadOnlyList<string> Warnings);

    public static Result Apply(DbTable table, DataTable data, string path, bool replaceAll = false)
    {
        var script = SqlScriptReader.Read(path);
        var columns = table.Columns;
        var byName = new Dictionary<string, DbColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
            byName[column.Key] = column;

        var mapping = new List<(int Index, DbColumn Column)>();
        var warnings = new List<string>();
        for (var i = 0; i < script.Columns.Length; i++)
        {
            var name = script.Columns[i].Trim();
            if (byName.TryGetValue(name, out var column))
                mapping.Add((i, column));
            else
                warnings.Add("忽略未知列：" + name);
        }

        if (mapping.Count == 0)
            throw new InvalidDataException(
                $"SQL 中的列与 {table.TableName} 的定义不匹配（文件表名：{script.TableName}）。");

        var idColumn = table.IdColumn;
        var hasId = idColumn != null && mapping.Any(m => m.Column.Key == idColumn.Key);

        if (replaceAll)
            data.Rows.Clear();

        // 同一个 ID 可能对应多行（组合键表），按"第几次出现"排队匹配，避免后一行挤掉前一行
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

        var idIndex = hasId ? mapping.First(m => m.Column.Key == idColumn.Key).Index : -1;
        var inserted = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var values in script.Rows)
        {
            int id = 0;
            if (hasId)
            {
                var raw = idIndex < values.Length ? values[idIndex] : null;
                if (!TryToInt(raw, out id))
                {
                    skipped++;
                    if (warnings.Count < 12)
                        warnings.Add($"跳过一行：ID 无效（[{raw}]）");
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
                    // 新插入的行不进队列，否则同 ID 的后续行会互相覆盖
                    target[idColumn.Key] = id;
                }
                data.Rows.Add(target);
                inserted++;
            }

            foreach (var (index, column) in mapping)
            {
                if (hasId && column.Key == idColumn.Key)
                    continue;
                if (index >= values.Length)
                    continue;
                try
                {
                    target[column.Key] = ConvertTo(values[index], column.FieldType);
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
                {
                    skipped++;
                    if (warnings.Count < 12)
                        warnings.Add($"列 {column.Key} 值无法解析（[{values[index]}]），已用默认值");
                    target[column.Key] = column.FieldType == typeof(string)
                        ? string.Empty
                        : Activator.CreateInstance(column.FieldType);
                }
            }
        }

        return new Result(inserted, updated, skipped, warnings);
    }

    private static bool TryToInt(object raw, out int value)
    {
        value = 0;
        if (raw == null)
            return false;
        return int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out value);
    }

    private static object ConvertTo(object raw, Type type)
    {
        if (type == typeof(string))
            return raw == null ? string.Empty : Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
        if (raw == null)
            return Activator.CreateInstance(type);
        if (raw is string s)
            return string.IsNullOrWhiteSpace(s)
                ? Activator.CreateInstance(type)
                : Convert.ChangeType(s.Trim(), type, CultureInfo.InvariantCulture);
        return Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
    }
}
