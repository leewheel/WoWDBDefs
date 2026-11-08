using System.Data;

namespace WowDBEditor.Core;

/// <summary>
/// 基于 <see cref="DataTable"/> 事件的轻量撤销栈：记录单元格改动与行的增删。
///
/// 早期版本用主键 ID 定位行，但很多表（例如 CharBaseInfo 以 RaceID 为键）ID 并不唯一，
/// 撤销时会改到同 ID 的另一行。现在直接持有 <see cref="DataRow"/> 引用与行序号，
/// 与 ID 是否唯一无关。
/// </summary>
public sealed class TableUndo : IDisposable
{
    private readonly DataTable table;
    private readonly string idKey;
    private readonly List<Entry> undo = new();
    private readonly List<Entry> redo = new();
    private readonly int limit;

    /// <param name="idKey">仅用于生成提示文案；定位行不再依赖它。</param>
    public TableUndo(DataTable table, string idKey = null, int limit = 5000)
    {
        this.table = table;
        this.idKey = idKey;
        this.limit = limit;
        table.ColumnChanging += OnColumnChanging;
        table.RowDeleting += OnRowDeleting;
        table.RowChanged += OnRowChanged;
    }

    /// <summary>正在应用撤销/重做时为 true，用于阻止应用方重复记录或重复标记脏数据。</summary>
    public bool IsApplying { get; private set; }

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool Enabled => true;

    public string UndoLabel => CanUndo ? undo[^1].Label : null;
    public string RedoLabel => CanRedo ? redo[^1].Label : null;
    public int UndoCount => undo.Count;

    public event EventHandler Changed;

    private void OnColumnChanging(object sender, DataColumnChangeEventArgs e)
    {
        if (IsApplying)
            return;
        if (e.Row.RowState is DataRowState.Added or DataRowState.Detached)
            return;

        var oldValue = e.Row[e.Column, DataRowVersion.Original];
        if (Equals(oldValue, e.ProposedValue))
            return;

        Push(new EditEntry
        {
            Row = e.Row,
            Column = e.Column.ColumnName,
            OldValue = oldValue,
            NewValue = e.ProposedValue,
            Label = $"修改 {e.Column.ColumnName}"
        });
    }

    private void OnRowDeleting(object sender, DataRowChangeEventArgs e)
    {
        if (IsApplying)
            return;
        if (e.Row.RowState is DataRowState.Detached or DataRowState.Deleted)
            return; // Remove 阶段会二次触发，忽略

        Push(new DeleteEntry
        {
            Row = e.Row,
            Index = SafeIndexOf(e.Row),
            Values = Snapshot(e.Row),
            Label = "删除行"
        });
    }

    private void OnRowChanged(object sender, DataRowChangeEventArgs e)
    {
        if (IsApplying || e.Action != DataRowAction.Add)
            return;

        Push(new AddEntry
        {
            Row = e.Row,
            Index = SafeIndexOf(e.Row),
            Values = Snapshot(e.Row),
            Label = "新增行"
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
                // 行已被删除就没什么可回滚的，静默跳过而不是报错
                if (edit.Row.RowState is DataRowState.Deleted or DataRowState.Detached)
                    return;
                if (!table.Columns.Contains(edit.Column))
                    return;
                edit.Row[edit.Column] = forward ? edit.NewValue : edit.OldValue;
                break;
            }

            case AddEntry add:
            {
                if (forward)
                    add.Row = InsertRow(add.Values, add.Index);
                else
                    RemoveRow(add.Row);
                break;
            }

            case DeleteEntry delete:
            {
                if (forward)
                {
                    RemoveRow(delete.Row);
                    break;
                }

                // 行还在集合里（只是被标记删除）就直接复活，位置和原值都能保住；
                // 若已被 Remove 摘掉，则按快照在原来的位置重建。
                if (delete.Row is { RowState: DataRowState.Deleted } && ReferenceEquals(delete.Row.Table, table))
                {
                    delete.Row.RejectChanges();
                }
                else
                {
                    delete.Row = InsertRow(delete.Values, delete.Index);
                }
                break;
            }
        }
    }

    private int SafeIndexOf(DataRow row)
    {
        try
        {
            return table.Rows.IndexOf(row);
        }
        catch
        {
            return -1;
        }
    }

    private void RemoveRow(DataRow row)
    {
        if (row == null || row.RowState == DataRowState.Detached)
            return;
        if (!ReferenceEquals(row.Table, table))
            return;
        try
        {
            table.Rows.Remove(row);
        }
        catch (IndexOutOfRangeException)
        {
            // 行已经不在集合里（可能被删除操作带走），忽略
        }
    }

    private DataRow InsertRow(object[] values, int index)
    {
        var row = table.NewRow();
        for (var i = 0; i < table.Columns.Count && i < values.Length; i++)
        {
            var value = values[i];
            row[i] = value ?? (table.Columns[i].DataType == typeof(string) ? string.Empty : DBNull.Value);
        }

        if (index >= 0 && index <= table.Rows.Count)
            table.Rows.InsertAt(row, index);
        else
            table.Rows.Add(row);
        return row;
    }

    private object[] Snapshot(DataRow row)
    {
        var values = new object[table.Columns.Count];
        for (var i = 0; i < table.Columns.Count; i++)
            values[i] = row[i, DataRowVersion.Current];
        return values;
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
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
        public DataRow Row;
        public string Column;
        public object OldValue;
        public object NewValue;
    }

    private sealed class AddEntry : Entry
    {
        public DataRow Row;
        public int Index;
        public object[] Values;
    }

    private sealed class DeleteEntry : Entry
    {
        public DataRow Row;
        public int Index;
        public object[] Values;
    }
}
