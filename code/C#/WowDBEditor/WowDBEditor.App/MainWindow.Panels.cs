using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WowDBEditor.Core;

namespace WowDBEditor.App;

public partial class MainWindow
{
    private bool structureViewActive;

    // ---------- 右侧面板显隐 ----------

    private void SetDetailPaneVisible(bool visible, bool updateMenu)
    {
        DetailColumn.Width = visible ? new GridLength(380) : new GridLength(0);
        DetailColumn.MinWidth = visible ? 240 : 0;
        DetailPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BtnToggleDetail.IsChecked = visible;
        if (updateMenu)
            MenuToggleDetail.IsChecked = visible;
    }

    private void ToggleDetail_Click(object sender, RoutedEventArgs e)
    {
        var visible = ReferenceEquals(sender, MenuToggleDetail) ? MenuToggleDetail.IsChecked : BtnToggleDetail.IsChecked == true;
        SetDetailPaneVisible(visible, updateMenu: true);
        if (visible)
            RefreshPane();
    }

    private void TabFields_Click(object sender, RoutedEventArgs e) => ShowPaneTab(structure: false);

    private void TabStructure_Click(object sender, RoutedEventArgs e) => ShowPaneTab(structure: true);

    private void ShowPaneTab(bool structure)
    {
        structureViewActive = structure;
        TabFields.IsChecked = !structure;
        TabStructure.IsChecked = structure;
        FieldScroll.Visibility = structure ? Visibility.Collapsed : Visibility.Visible;
        StructureScroll.Visibility = structure ? Visibility.Visible : Visibility.Collapsed;
        DetailTitle.Text = structure ? "表结构" : "行详情";
        DetailHint.Text = structure
            ? "字段名带 ? 表示 WoWDBDefs 中未验证；括号内为数组下标。"
            : "选中一行即可逐字段编辑；Enter 提交并跳到下一个字段。";
        RefreshPane();
    }

    private void RefreshPane()
    {
        if (structureViewActive)
            BuildStructurePanel();
        else
            BuildFieldPanel();
    }

    // ---------- 选中行变化 ----------

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectionText.Text = Grid.SelectedItems.Count > 1 ? $"已选 {Grid.SelectedItems.Count} 行" : "";
        if (!structureViewActive)
            BuildFieldPanel();
    }

    private void RefreshSelectedRow()
    {
        if (!structureViewActive)
            BuildFieldPanel();
    }

    // ---------- 字段编辑面板 ----------

    private void BuildFieldPanel()
    {
        FieldPanel.Children.Clear();
        fieldEditors.Clear();

        if (currentTable == null)
        {
            DetailTitle.Text = "行详情";
            DetailHint.Text = "未打开文件。";
            return;
        }

        if (Grid.SelectedItem is not DataRowView rowView || rowView.Row.RowState == DataRowState.Deleted)
        {
            DetailTitle.Text = "行详情";
            DetailHint.Text = "在左侧表格中选中一行，即可在这里逐字段编辑；Enter 提交并跳到下一个字段。";
            return;
        }

        DetailTitle.Text = currentTable.IdColumn != null
            ? $"行详情 · ID {FormatValue(rowView[currentTable.IdColumn.Key])}"
            : "行详情";
        DetailHint.Text = $"共 {currentTable.Columns.Count} 个字段；Enter 提交并跳到下一个字段。";

        var muted = (Brush)FindResource("MutedForeground");
        var accent = (Brush)FindResource("Accent");

        foreach (var column in currentTable.Columns)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var tooltip = BuildColumnTooltip(column);
            var label = new TextBlock
            {
                Text = column.Key,
                FontSize = 12,
                Foreground = column.IsId ? accent : muted,
                ToolTip = tooltip,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            System.Windows.Controls.Grid.SetColumn(label, 0);

            var box = new TextBox
            {
                Text = FormatValue(rowView[column.Key]),
                Tag = column,
                ToolTip = tooltip,
                FontSize = 12.5
            };
            box.LostFocus += FieldBox_LostFocus;
            box.KeyDown += FieldBox_KeyDown;
            System.Windows.Controls.Grid.SetColumn(box, 1);

            row.Children.Add(label);
            row.Children.Add(box);
            FieldPanel.Children.Add(row);
            fieldEditors[column.Key] = box;
        }
    }

    private void FieldBox_LostFocus(object sender, RoutedEventArgs e) => CommitField((TextBox)sender);

    private void FieldBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        var box = (TextBox)sender;
        if (!CommitField(box))
            return;
        MoveToNextEditor(box);
    }

    private void MoveToNextEditor(TextBox current)
    {
        var boxes = FieldPanel.Children.OfType<Grid>()
            .Select(g => g.Children.OfType<TextBox>().FirstOrDefault())
            .Where(b => b != null)
            .ToList();
        var index = boxes.IndexOf(current);
        if (index >= 0 && index + 1 < boxes.Count)
        {
            var next = boxes[index + 1];
            next.Focus();
            next.SelectAll();
        }
        else
        {
            Grid.Focus();
        }
    }

    private bool CommitField(TextBox box)
    {
        if (box.Tag is not DbColumn column)
            return true;
        if (Grid.SelectedItem is not DataRowView rowView || rowView.Row.RowState == DataRowState.Deleted)
            return true;

        object value;
        try
        {
            value = ParseFieldText(box.Text, column.FieldType);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            box.BorderBrush = (Brush)FindResource("ErrorBrush");
            SetStatus($"{column.Key} 输入无效：{box.Text}（期望 {column.TypeLabel}）");
            return false;
        }

        box.ClearValue(TextBox.BorderBrushProperty);
        var current = rowView[column.Key];
        if (!Equals(current, value))
        {
            try
            {
                rowView[column.Key] = value;
            }
            catch (Exception ex)
            {
                box.BorderBrush = (Brush)FindResource("ErrorBrush");
                SetStatus($"{column.Key} 写入失败：{ex.Message}");
                return false;
            }
        }
        return true;
    }

    private static object ParseFieldText(string text, Type type)
    {
        text ??= "";
        if (type == typeof(string))
            return text;
        if (string.IsNullOrWhiteSpace(text))
            return Activator.CreateInstance(type);
        return Convert.ChangeType(text.Trim(), type, CultureInfo.InvariantCulture);
    }

    private static string FormatValue(object value)
    {
        if (value == null || value is DBNull)
            return "";
        return value switch
        {
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    /// <summary>表格里改了值，同步到右侧面板（不打断正在输入的框）。</summary>
    private void OnCellChanged(object sender, DataColumnChangeEventArgs e)
    {
        if (suppressDirty)
            return;
        if (Grid.SelectedItem is not DataRowView rowView || !ReferenceEquals(rowView.Row, e.Row))
            return;
        if (fieldEditors.TryGetValue(e.Column.ColumnName, out var box) && !box.IsKeyboardFocusWithin)
            box.Text = FormatValue(e.ProposedValue);
    }

    // ---------- 结构面板 ----------

    private void BuildStructurePanel()
    {
        StructurePanel.Children.Clear();
        if (currentTable == null)
            return;


        StructurePanel.Children.Add(new TextBlock
        {
            Text = currentTable.TableName,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });
        StructurePanel.Children.Add(new TextBlock
        {
            Text = $"{currentTable.Format}    Layout {currentTable.LayoutHash:X8}    {currentTable.Columns.Count} 列",
            FontSize = 11.5,
            Foreground = (Brush)FindResource("MutedForeground"),
            Margin = new Thickness(0, 0, 0, 10)
        });

        var muted = (Brush)FindResource("MutedForeground");
        var accent = (Brush)FindResource("Accent");

        foreach (var column in currentTable.Columns)
        {
            var card = new Border
            {
                Background = (Brush)FindResource("ControlBackground"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5, 8, 6),
                Margin = new Thickness(0, 0, 0, 5)
            };
            var stack = new StackPanel();

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock
            {
                Text = column.Key,
                FontSize = 12.5,
                FontWeight = column.IsId ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = column.IsId ? accent : (Brush)FindResource("Foreground")
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = "  " + column.TypeLabel,
                FontSize = 11.5,
                Foreground = muted
            });
            if (!column.Verified)
                titleRow.Children.Add(new TextBlock { Text = "  未验证", FontSize = 11, Foreground = (Brush)FindResource("ErrorBrush") });
            stack.Children.Add(titleRow);

            if (!string.IsNullOrEmpty(column.ForeignTable))
                stack.Children.Add(new TextBlock
                {
                    Text = $"→ {column.ForeignTable}::{column.ForeignColumn}",
                    FontSize = 11,
                    Foreground = accent,
                    Margin = new Thickness(0, 1, 0, 0)
                });

            if (!string.IsNullOrEmpty(column.Comment))
                stack.Children.Add(new TextBlock
                {
                    Text = column.Comment,
                    FontSize = 11,
                    Foreground = muted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });

            card.Child = stack;
            StructurePanel.Children.Add(card);
        }
    }

    // ---------- 列显示控制 ----------

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        if (currentTable == null)
        {
            BtnColumns.IsChecked = false;
            return;
        }
        if (ColumnPopup.IsOpen)
        {
            ColumnPopup.IsOpen = false;
            return;
        }
        BuildColumnChooser();
        ColumnPopup.IsOpen = true;
    }

    private void BuildColumnChooser()
    {
        ColumnList.Children.Clear();
        if (currentTable == null)
            return;
        foreach (var column in currentTable.Columns)
        {
            var check = new CheckBox
            {
                Content = column.Key + (column.Verified ? "" : " ?"),
                IsChecked = !hiddenColumns.Contains(column.Key),
                Tag = column.Key,
                FontSize = 12,
                Margin = new Thickness(0, 1, 0, 1),
                ToolTip = BuildColumnTooltip(column)
            };
            check.Checked += ColumnVisibility_Changed;
            check.Unchecked += ColumnVisibility_Changed;
            ColumnList.Children.Add(check);
        }
    }

    private void ColumnVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string key })
            return;

        if (sender is CheckBox { IsChecked: true })
            hiddenColumns.Remove(key);
        else
            hiddenColumns.Add(key);

        if (currentTable != null && hiddenColumns.Count >= currentTable.Columns.Count)
        {
            hiddenColumns.Remove(key);
            ((CheckBox)sender).IsChecked = true;
            SetStatus("至少要保留一列显示");
            return;
        }

        BuildGridColumns();
        SetStatus($"当前显示 {currentTable?.Columns.Count - hiddenColumns.Count} / {currentTable?.Columns.Count} 列");
    }

    private void ColumnsAll_Click(object sender, RoutedEventArgs e)
    {
        hiddenColumns.Clear();
        BuildGridColumns();
        BuildColumnChooser();
    }

    private void ColumnsInvert_Click(object sender, RoutedEventArgs e)
    {
        if (currentTable == null)
            return;
        var all = currentTable.Columns.Select(c => c.Key).ToList();
        var newHidden = all.Where(k => !hiddenColumns.Contains(k)).ToList();
        if (newHidden.Count >= all.Count)
        {
            SetStatus("至少要保留一列显示");
            return;
        }
        hiddenColumns.Clear();
        foreach (var key in newHidden)
            hiddenColumns.Add(key);
        BuildGridColumns();
        BuildColumnChooser();
    }

    private void ShowAllColumns_Click(object sender, RoutedEventArgs e) => ColumnsAll_Click(sender, e);
}
