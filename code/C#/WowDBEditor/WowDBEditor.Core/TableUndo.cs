using System.Data;
using System.Globalization;

namespace WowDBEditor.Core;

/// <summary>
/// 基于 <see cref="DataTable"/> 事件的轻量撤销栈：只记录单元格改动与行的增删，
/// 行通过主键 ID 定位，避免保存/排序后行对象失效。
/// </summary>
public sealed class TableUndo : IDisposable
{
    private readonly DataTable table;
    private readonly string idKey;
    private readonly List<Entry> undo = new();
    private readonly List<Entry> redo = new();
    private readonly int limit;

    public TableUndo(DataTable table, string idKey, int limit = 5000)
    {
        this.table = table;
        this.idKey = idKey;
        this.limit = limit;
        if (idKey == null)
            return;
        table.ColumnChanging += OnColumnChanging;
        table.RowDeleting += OnRowDeleting;
        table.RowChanged += OnRowChanged;
    }

    /// <summary>正在应用撤销/重做时为 true，用于阻止应用方重复记录或重复标记脏数据。</summary>
    public bool IsApplying { get; private set; }

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool Enabled => idKey != null;

    public string UndoLabel => CanUndo ? undo[^1].Label : null;
    public string RedoLabel => CanRedo ? redo[^1].Label : null;
    public int UndoCount => undo.Count;

    public event EventHandler Changed;

    private void OnColumnChanging(object sender, DataColumnChangeEventArgs e)
    {
        if (IsApplying || idKey == null || e.Row.RowState == DataRowState.Added || e.Row.RowState == DataRowState.Detached)
            return;
        if (e.Column.ColumnName == idKey)
            return; // ID 改动不进撤销栈：主键约束下回滚不可靠

        var oldValue = e.Row[e.Column, DataRowVersion.Original];
        if (Equals(oldValue, e.ProposedValue))
            return;

        var id = ReadId(e.Row);
        if (id == null)
            return;

        Push(new EditEntry
        {
            Id = id.Value,
            Column = e.Column.ColumnName,
            OldValue = oldValue,
            NewValue = e.ProposedValue,
            Label = $"修改 {e.Column.ColumnName}（ID {id.Value}）"
        });
    }

    private void OnRowDeleting(object sender, DataRowChangeEventArgs e)
    {
        if (IsApplying || idKey == null)
            return;
        if (e.Row.RowState is DataRowState.Detached or DataRowState.Deleted)
            return; // Remove 阶段会二次触发，忽略
        var id = ReadId(e.Row);
        if (id == null)
            return;

        Push(new DeleteEntry
        {
            Id = id.Value,
            Values = Snapshot(e.Row),
            Label = $"删除行（ID {id.Value}）"
        });
    }

    private void OnRowChanged(object sender, DataRowChangeEventArgs e)
    {
        if (IsApplying || idKey == null || e.Action != DataRowAction.Add)
            return;
        var id = ReadId(e.Row);
        if (id == null)
            return;

        Push(new AddEntry
        {
            Id = id.Value,
            Label = $"新增行（ID {id.Value}）"
        });
    }

    private void Push(Entry entry)
    {
        undo.Add(entry);
        if (undo.Count > limit)
            undo.RemoveAt(0);
        redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Undo()
    {
        if (!CanUndo)
            return null;
        var entry = undo[^1];
        undo.RemoveAt(undo.Count - 1);

        IsApplying = true;
        try
        {
            Apply(entry, forward: false);
        }
        finally
        {
            IsApplying = false;
        }
        redo.Add(entry);
        Changed?.Invoke(this, EventArgs.Empty);
        return entry.Label;
    }

    public string Redo()
    {
        if (!CanRedo)
            return null;
        var entry = redo[^1];
        redo.RemoveAt(redo.Count - 1);

        IsApplying = true;
        try
        {
            Apply(entry, forward: true);
        }
        finally
        {
            IsApplying = false;
        }
        undo.Add(entry);
        Changed?.Invoke(this, EventArgs.Empty);
        return entry.Label;
    }

    private void Apply(Entry entry, bool forward)
    {
        switch (entry)
        {
            case EditEntry edit:
            {
                var row = FindRow(edit.Id);
                if (row == null)
                    throw new InvalidOperationException($"找不到 ID {edit.Id} 的行，无法{(forward ? "重做" : "撤销")}。");
                row[edit.Column] = forward ? edit.NewValue : edit.OldValue;
                break;
            }
            case AddEntry add:
            {
                if (forward)
                {
                    if (add.Values == null)
                        return;
                    AddRow(add.Values);
                }
                else
                {
                    var row = FindRow(add.Id);
                    if (row == null)
                        return;
                    add.Values ??= Snapshot(row);
                    table.Rows.Remove(row);
                }
                break;
            }
            case DeleteEntry delete:
            {
                if (forward)
                {
                    var row = FindRow(delete.Id);
                    if (row != null)
                        table.Rows.Remove(row);
                }
                else
                {
                    if (FindRow(delete.Id) == null)
                        AddRow(delete.Values);
                }
                break;
            }
        }
    }

    private void AddRow(object[] values)
    {
        // 撤销删除时，表里可能还留着同 ID 的墓碑行，必须先摘掉再插入
        var idIndex = table.Columns.IndexOf(idKey);
        if (idIndex >= 0 && idIndex < values.Length && values[idIndex] != null)
        {
            try
            {
                PurgeDeleted(Convert.ToInt32(values[idIndex], CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                // ID 不是整数则交给 DataTable 自己校验
            }
        }

        var row = table.NewRow();
        for (var i = 0; i < table.Columns.Count && i < values.Length; i++)
            row[i] = values[i] ?? (table.Columns[i].DataType == typeof(string) ? string.Empty : DBNull.Value);
        table.Rows.Add(row);
    }

    private object[] Snapshot(DataRow row)
    {
        var values = new object[table.Columns.Count];
        for (var i = 0; i < table.Columns.Count; i++)
            values[i] = row[i, DataRowVersion.Current];
        return values;
    }

    private void PurgeDeleted(int id)
    {
        for (var i = table.Rows.Count - 1; i >= 0; i--)
        {
            var row = table.Rows[i];
            if (row.RowState == DataRowState.Deleted && ReadId(row) == id)
                table.Rows.Remove(row);
        }
    }

    private int? ReadId(DataRow row)
    {
        try
        {
            var value = row.RowState == DataRowState.Deleted
                ? row[idKey, DataRowVersion.Original]
                : row[idKey];
            if (value == null || value is DBNull)
                return null;
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private DataRow FindRow(int id)
    {
        foreach (DataRow row in table.Rows)
        {
            if (row.RowState is DataRowState.Deleted or DataRowState.Detached)
                continue;
            var value = ReadId(row);
            if (value == id)
                return row;
        }
        return null;
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (idKey == null)
            return;
        table.ColumnChanging -= OnColumnChanging;
        table.RowDeleting -= OnRowDeleting;
        table.RowChanged -= OnRowChanged;
    }

    private abstract class Entry
    {
        public string Label { get; init; }
    }

    private sealed class EditEntry : Entry
    {
        public int Id;
        public string Column;
        public object OldValue;
        public object NewValue;
    }

    private sealed class AddEntry : Entry
    {
        public int Id;
        public object[] Values;
    }

    private sealed class DeleteEntry : Entry
    {
        public int Id;
        public object[] Values;
    }
}
