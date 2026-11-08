using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WowDBEditor.App.Dialogs;
using WowDBEditor.Core;
using WowDBEditor.Core.MySql;
using WowDBEditor.Core.Sql;

namespace WowDBEditor.App;

public partial class MainWindow : Window
{
    private AppSettings settings;
    private DefinitionService definitions;
    private DbTable currentTable;
    private DataTable dataTable;
    private DataView dataView;
    private TableUndo undo;
    private string openedBuild;
    private bool isDirty;
    private bool suppressDirty;
    private readonly HashSet<string> hiddenColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBox> fieldEditors = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();

        settings = AppSettings.Load();
        var defDir = settings.DefinitionsDirectory;
        if (string.IsNullOrEmpty(defDir) || !Directory.Exists(defDir))
            defDir = DefinitionService.FindDefinitionsDirectory(AppContext.BaseDirectory);
        if (defDir != null)
            definitions = new DefinitionService(defDir);

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, (_, _) => OpenFile()));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, (_, _) => SaveFile(false),
            (_, e) => e.CanExecute = currentTable != null));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.SaveAs, (_, _) => SaveFile(true),
            (_, e) => e.CanExecute = currentTable != null));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => ApplyUndo(),
            (_, e) => e.CanExecute = undo is { CanUndo: true }));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => ApplyRedo(),
            (_, e) => e.CanExecute = undo is { CanRedo: true }));

        InputBindings.Add(new KeyBinding(ApplicationCommands.Open, Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save, Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ApplicationCommands.SaveAs, Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Undo, Key.Z, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Redo, Key.Y, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => FilterBox.Focus()), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => DuplicateRow()), Key.D, ModifierKeys.Control));

        Grid.RowHeaderWidth = 52;
        if (settings.DetailPaneWidth is > 200 and < 900)
            DetailColumn.Width = new GridLength(settings.DetailPaneWidth);
        // 高分屏 / 小屏适配：避免窗口比显示器工作区还大，把右侧面板挤出屏幕
        Loaded += (_, _) => ScreenFit.Apply(this, 1440, 880, 960, 560);
        SetDetailPaneVisible(settings.ShowDetailPane, updateMenu: true);
        UpdateUiState();
        UpdateTitle();
        RebuildRecentMenu();

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
            Dispatcher.BeginInvoke(new Action(() => OpenPath(args[1])), DispatcherPriority.Loaded);
    }

    private void UpdateUiState()
    {
        var hasTable = currentTable != null;
        foreach (var control in new Control[] { BtnAddRow, BtnDuplicateRow, BtnDeleteRow, BtnExportCsv, BtnImportCsv,
                     BtnExportSql, BtnImportSql, BtnExportMySql, BtnImportMySql, FilterBox, BtnColumns })
            control.IsEnabled = hasTable;
        MenuDuplicateRow.IsEnabled = hasTable;
        MenuDeleteRow.IsEnabled = hasTable;
        MenuExportCsv.IsEnabled = hasTable;
        MenuImportCsv.IsEnabled = hasTable;
        MenuExportSql.IsEnabled = hasTable;
        MenuImportSql.IsEnabled = hasTable;
        MenuExportMySql.IsEnabled = hasTable;
        MenuImportMySql.IsEnabled = hasTable;
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateTitle()
    {
        Title = currentTable == null
            ? "WoW DBC/DB2 修改器"
            : $"WoW DBC/DB2 修改器 — {Path.GetFileName(currentTable.FilePath)}{(isDirty ? " *" : "")}";
        DirtyText.Text = isDirty ? "已修改（未保存）" : "";
    }

    private void UpdateInfoBar()
    {
        if (currentTable == null)
        {
            InfoText.Text = "未打开文件";
            return;
        }
        // 优先显示真正生效的版本（区间定义会落在具体某个 build 上），而不是定义块里列的版本
        var version = openedBuild ?? currentTable.BuildString ?? GetVersionLabel();
        InfoText.Text = $"{currentTable.FilePath}    格式: {currentTable.Format}    版本: {version}    " +
                        $"行数: {currentTable.RowCount:N0}    列数: {currentTable.Columns.Count}    " +
                        $"记录: {currentTable.RecordSize}B    Layout: {currentTable.LayoutHash:X8}    Table: {currentTable.TableHash:X8}";
    }

    private string GetVersionLabel()
    {
        var version = currentTable.Version;
        if (version.builds is { Length: > 0 })
            return string.Join(", ", version.builds.Take(3)) + (version.builds.Length > 3 ? "…" : "");
        if (version.buildRanges is { Length: > 0 })
            return version.buildRanges[0].ToString();
        return "(未知)";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void SetDirty(bool value)
    {
        if (isDirty == value) return;
        isDirty = value;
        UpdateTitle();
    }

    private bool EnsureDefinitions()
    {
        if (definitions != null && Directory.Exists(definitions.DefinitionsDirectory))
            return true;
        MessageBox.Show(this, "尚未配置有效的定义目录。请选择 WoDBDefs 项目的 definitions 文件夹。",
            "需要定义目录", MessageBoxButton.OK, MessageBoxImage.Information);
        return ShowSettings();
    }

    private bool ShowSettings()
    {
        var dialog = new SettingsDialog(definitions?.DefinitionsDirectory ?? settings.DefinitionsDirectory) { Owner = this };
        if (dialog.ShowDialog() != true)
            return false;
        settings.DefinitionsDirectory = dialog.DefinitionsDirectory;
        settings.Save();
        definitions = new DefinitionService(dialog.DefinitionsDirectory);
        SetStatus("定义目录已更新：" + dialog.DefinitionsDirectory);
        return true;
    }

    private bool ConfirmDiscardChanges()
    {
        if (!isDirty)
            return true;
        var result = MessageBox.Show(this, "当前文件有未保存的修改，是否先保存？", "未保存的修改",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel)
            return false;
        if (result == MessageBoxResult.Yes)
        {
            SaveFile(false);
            return !isDirty;
        }
        return true;
    }

    private void OpenFile()
    {
        if (!ConfirmDiscardChanges())
            return;
        var dialog = new OpenFileDialog
        {
            Title = "打开 DBC/DB2 文件",
            Filter = "DBC/DB2 文件 (*.dbc;*.db2;*.adb;*.wdb)|*.dbc;*.db2;*.adb;*.wdb|所有文件 (*.*)|*.*"
        };
        if (SafeDialog.Show(dialog, this, settings.LastOpenDirectory))
            OpenPath(dialog.FileName);
    }

    private void OpenPath(string path)
    {
        if (!EnsureDefinitions())
            return;

        DbTable table = null;
        string build = null;
        try
        {
            try
            {
                table = DbTable.Open(path, definitions);
            }
            catch (DefinitionNotFoundException ex) when (ex.DbdExists)
            {
                // 之前为该表选过版本，直接复用，避免每张 WDBC 都要选一次
                if (settings.BuildOverrides.TryGetValue(ex.TableName, out var remembered))
                {
                    try
                    {
                        table = DbTable.Open(path, definitions, remembered);
                        build = remembered;
                    }
                    catch
                    {
                        table = null;
                    }
                }

                if (table == null)
                {
                    var definition = definitions.GetDefinition(ex.TableName);
                    var choices = definitions.ListVersionChoices(definition, ex.FileFormat, ex.FileRecordSize);
                    if (choices.Count == 0)
                        throw new InvalidOperationException($"定义文件 {ex.TableName}.dbd 中没有可用的版本。", ex);

                    var prompt = $"{Path.GetFileName(path)} 的文件头里没有 LayoutHash（老格式），无法自动判断版本。\n" +
                                 $"文件记录长度 {ex.FileRecordSize} 字节；列表里带 ✔ 的是长度吻合的版本。\n" +
                                 "选中条目后也可以在下面直接改成自己的版本号，之后同表自动沿用：";

                    while (table == null)
                    {
                        var picker = new BuildPickDialog(choices, prompt) { Owner = this };
                        if (picker.ShowDialog() != true)
                            return;

                        var candidate = picker.SelectedBuild;
                        try
                        {
                                table = DbTable.Open(path, definitions, candidate);
                            build = candidate;
                            // 记录长度对不上说明版本可能选错了，这种选择不要记住，免得下次静默用错
                            if (table.RecordSize == table.DefinitionRecordSize)
                            {
                                settings.BuildOverrides[ex.TableName] = candidate;
                                settings.Save();
                            }
                        }
                        catch (Exception openError)
                        {
                            MessageBox.Show(this,
                                $"版本 {candidate} 套不上这个文件：" + Environment.NewLine + openError.Message +
                                Environment.NewLine + Environment.NewLine + "请换一个版本再试。",
                                "版本不匹配", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                }
            }
            catch (DefinitionNotFoundException ex) when (!ex.DbdExists)
            {
                MessageBox.Show(this,
                    $"缺少定义文件 {ex.TableName}.dbd。\n请在“工具 → 设置”中指定包含该定义的 definitions 目录后重试。",
                    "缺少定义", MessageBoxButton.OK, MessageBoxImage.Warning);
                if (ShowSettings())
                    OpenPath(path);
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 记录长度不一致 = 文件里有定义没覆盖到的字段（自定义列）或版本选错了，必须先提醒
        if (table.DefinitionRecordSize > 0 && table.RecordSize != table.DefinitionRecordSize)
        {
            var nl = Environment.NewLine;
            var proceed = MessageBox.Show(this,
                "记录长度与定义不一致：" + nl +
                $"    文件：{table.RecordSize} 字节" + nl +
                $"    定义：{table.DefinitionRecordSize} 字节" + nl + nl +
                "通常说明这个 DBC 带了自定义列，或者版本选错了。" + nl +
                "继续打开可以查看、修改已有字段，但保存时会按定义重写文件，多出来的列会丢失。" + nl + nl +
                "确定继续打开吗？（建议先备份原文件）",
                "记录长度与定义不一致", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes)
            {
                table.Dispose();
                return;
            }
        }

        LoadTable(table, build);
        settings.LastOpenDirectory = SafeDialog.Normalize(Path.GetDirectoryName(Path.GetFullPath(path)));
        settings.AddRecentFile(path);
        settings.Save();
        RebuildRecentMenu();
        SetStatus($"已打开 {Path.GetFileName(path)}（{table.RowCount:N0} 行）");
    }

    private void LoadTable(DbTable table, string build)
    {
        suppressDirty = true;
        try
        {
            if (dataView != null)
                dataView.ListChanged -= OnListChanged;
            undo?.Dispose();
            undo = null;
            currentTable?.Dispose();

            currentTable = table;
            openedBuild = build;
            hiddenColumns.Clear();
            dataTable = table.CreateDataTable();
            dataView = new DataView(dataTable);
            dataView.ListChanged += OnListChanged;
            undo = new TableUndo(dataTable, table.IdColumn?.Key);
            undo.Changed += (_, _) => CommandManager.InvalidateRequerySuggested();
            dataTable.ColumnChanged += OnCellChanged;

            Grid.ItemsSource = dataView;
            BuildGridColumns();
            BuildColumnChooser();
            BuildStructurePanel();
            FieldPanel.Children.Clear();
            fieldEditors.Clear();
            FilterBox.Text = "";
            SelectionText.Text = "";
            MatchCount.Text = $"{dataView.Count:N0} / {dataTable.Rows.Count:N0} 行";
            isDirty = false;
        }
        finally
        {
            suppressDirty = false;
        }
        UpdateUiState();
        UpdateInfoBar();
        UpdateTitle();

        // 默认选中第一行，右侧字段面板立刻可用
        if (Grid.Items.Count > 0)
        {
            Grid.SelectedIndex = 0;
            Grid.ScrollIntoView(Grid.Items[0]);
        }
        else
        {
            ShowPaneTab(structureViewActive);
        }
    }

    private void BuildGridColumns()
    {
        Grid.Columns.Clear();
        foreach (var column in currentTable.Columns)
        {
            if (hiddenColumns.Contains(column.Key))
                continue;

            var binding = new Binding(column.Key)
            {
                Mode = BindingMode.TwoWay,
                ConverterCulture = CultureInfo.InvariantCulture,
                ValidatesOnExceptions = true
            };
            var header = new TextBlock
            {
                Text = column.Key + (column.Verified ? "" : " ?"),
                ToolTip = BuildColumnTooltip(column),
                Foreground = column.Verified
                    ? (Brush)FindResource("Foreground")
                    : (Brush)FindResource("MutedForeground")
            };
            var gridColumn = new DataGridTextColumn
            {
                Header = header,
                Binding = binding,
                SortMemberPath = column.Key,
                MinWidth = 60,
                Width = column.FieldType == typeof(string) ? new DataGridLength(220) : DataGridLength.Auto,
                ElementStyle = (Style)FindResource("GridTextStyle"),
                EditingElementStyle = (Style)FindResource("GridEditingStyle")
            };
            if (column.IsId)
                gridColumn.FontWeight = FontWeights.SemiBold;
            Grid.Columns.Add(gridColumn);
        }
        Grid.FrozenColumnCount = currentTable.IdColumn != null && !hiddenColumns.Contains(currentTable.IdColumn.Key) ? 1 : 0;
    }

    private static string BuildColumnTooltip(DbColumn column)
    {
        var tip = $"{column.Key}    类型: {column.TypeLabel}";
        if (column.IsArray)
            tip += $"    数组元素 [{column.ArrayIndex}]";
        if (!column.Verified)
            tip += "\n（未验证字段）";
        if (!string.IsNullOrEmpty(column.Comment))
            tip += "\n" + column.Comment;
        if (!string.IsNullOrEmpty(column.ForeignTable))
            tip += $"\n外键: {column.ForeignTable}::{column.ForeignColumn}";
        return tip;
    }

    private void OnListChanged(object sender, ListChangedEventArgs e)
    {
        if (suppressDirty)
            return;
        if (e.ListChangedType is ListChangedType.ItemChanged or ListChangedType.ItemAdded or ListChangedType.ItemDeleted)
            SetDirty(true);
        if (e.ListChangedType is ListChangedType.ItemAdded or ListChangedType.ItemDeleted or ListChangedType.Reset)
            UpdateMatchCount();
    }

    private void UpdateMatchCount()
    {
        if (dataView != null)
        {
            var total = dataTable.Rows.Cast<DataRow>().Count(r => r.RowState != DataRowState.Deleted);
            MatchCount.Text = $"{dataView.Count:N0} / {total:N0} 行";
        }
    }

    private bool CommitGrid()
    {
        Grid.CommitEdit(DataGridEditingUnit.Cell, true);
        if (!Grid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            MessageBox.Show(this, "当前单元格输入无效，请修正后再操作。", "输入无效",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void SaveFile(bool saveAs)
    {
        if (currentTable == null)
            return;
        if (!CommitGrid())
            return;

        var path = currentTable.FilePath;
        if (saveAs)
        {
            var dialog = new SaveFileDialog
            {
                Title = "另存为",
                Filter = "DBC/DB2 文件 (*.dbc;*.db2)|*.dbc;*.db2|所有文件 (*.*)|*.*",
                FileName = Path.GetFileName(path)
            };
            if (!SafeDialog.Show(dialog, this, Path.GetDirectoryName(path)))
                return;
            path = dialog.FileName;
        }

        suppressDirty = true;
        try
        {
            currentTable.Save(dataTable, path);
            isDirty = false;
            // 保存后 DataTable 已 AcceptChanges，撤销栈的基准失效
            undo?.Clear();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            suppressDirty = false;
        }
        UpdateTitle();
        UpdateInfoBar();
        SetStatus($"已保存：{path}");
    }

    private void AddRow_Click(object sender, RoutedEventArgs e) => AddEmptyRow();

    private void AddEmptyRow()
    {
        if (currentTable == null || !CommitGrid())
            return;

        FilterBox.Text = "";
        var row = dataTable.NewRow();
        foreach (var column in currentTable.Columns)
            row[column.Key] = column.FieldType == typeof(string) ? "" : Activator.CreateInstance(column.FieldType);

        if (currentTable.IdColumn != null)
            row[currentTable.IdColumn.Key] = NextId();

        dataTable.Rows.Add(row);
        SelectRow(row);
        SetStatus("已添加新行");
    }

    private int NextId()
    {
        if (currentTable.IdColumn == null)
            return 0;
        return dataTable.Rows.Cast<DataRow>()
            .Where(r => r.RowState != DataRowState.Deleted)
            .Select(r => Convert.ToInt32(r[currentTable.IdColumn.Key], CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max() + 1;
    }

    private void SelectRow(DataRow row)
    {
        var view = dataView.Cast<DataRowView>().FirstOrDefault(v => ReferenceEquals(v.Row, row));
        if (view != null)
        {
            Grid.SelectedItem = view;
            Grid.ScrollIntoView(view);
        }
    }

    private void DuplicateRow_Click(object sender, RoutedEventArgs e) => DuplicateRow();

    private void DuplicateRow()
    {
        if (currentTable == null || !CommitGrid())
            return;
        if (Grid.SelectedItem is not DataRowView source)
        {
            SetStatus("请先选中要复制的行");
            return;
        }

        FilterBox.Text = "";
        var row = dataTable.NewRow();
        foreach (var column in currentTable.Columns)
            row[column.Key] = source.Row[column.Key];

        if (currentTable.IdColumn != null)
            row[currentTable.IdColumn.Key] = NextId();

        dataTable.Rows.Add(row);
        SelectRow(row);
        SetStatus("已复制当前行，请修改新行的 ID 与内容");
    }

    private void DeleteRows_Click(object sender, RoutedEventArgs e)
    {
        if (currentTable == null)
            return;
        var rows = Grid.SelectedItems.Cast<DataRowView>().Select(v => v.Row).ToList();
        if (rows.Count == 0)
        {
            SetStatus("请先选中要删除的行");
            return;
        }
        var confirm = MessageBox.Show(this, $"确定删除选中的 {rows.Count} 行吗？", "删除行",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
            return;
        foreach (var row in rows)
        {
            row.Delete();
            dataTable.Rows.Remove(row); // 立即摘掉墓碑行，避免与撤销的同 ID 新行冲突
        }
        UpdateMatchCount();
        SetStatus($"已删除 {rows.Count} 行（保存后生效）");
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ClearFilter_Click(object sender, RoutedEventArgs e) => FilterBox.Text = "";

    private void ApplyFilter()
    {
        if (dataView == null)
            return;
        var text = FilterBox.Text.Trim();
        try
        {
            if (text.Length == 0)
            {
                dataView.RowFilter = "";
            }
            else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && currentTable.IdColumn != null)
            {
                dataView.RowFilter = $"[{currentTable.IdColumn.Key}] = {id}";
            }
            else
            {
                var escaped = EscapeLike(text).Replace("'", "''");
                var parts = currentTable.Columns
                    .Where(c => c.FieldType == typeof(string))
                    .Select(c => $"[{c.Key}] LIKE '%{escaped}%'")
                    .ToList();
                if (parts.Count == 0 && currentTable.IdColumn != null)
                    parts.Add($"Convert([{currentTable.IdColumn.Key}], 'System.String') LIKE '%{escaped}%'");
                dataView.RowFilter = parts.Count > 0 ? string.Join(" OR ", parts) : "1 = 0";
            }
        }
        catch (EvaluateException)
        {
            dataView.RowFilter = "";
        }
        UpdateMatchCount();
    }

    private static string EscapeLike(string value) =>
        value.Replace("[", "[[]").Replace("%", "[%]").Replace("*", "[*]");

    private void Grid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
            Dispatcher.BeginInvoke(new Action(() => Grid.CommitEdit(DataGridEditingUnit.Row, true)), DispatcherPriority.Background);
    }

    private void Grid_LoadingRow(object sender, DataGridRowEventArgs e)
        => e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.InvariantCulture);

    private void ApplyUndo()
    {
        if (undo == null)
            return;
        try
        {
            var label = undo.Undo();
            Grid.Items.Refresh();
            RefreshSelectedRow();
            UpdateMatchCount();
            SetDirty(true);
            SetStatus($"已撤销：{label}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "撤销失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyRedo()
    {
        if (undo == null)
            return;
        try
        {
            var label = undo.Redo();
            Grid.Items.Refresh();
            RefreshSelectedRow();
            UpdateMatchCount();
            SetDirty(true);
            SetStatus($"已重做：{label}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "重做失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (currentTable == null || !CommitGrid())
            return;
        var dialog = new SaveFileDialog
        {
            Title = "导出 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = currentTable.TableName + ".csv"
        };
        if (!SafeDialog.Show(dialog, this, settings.LastExportDirectory))
            return;

        try
        {
            CsvIO.Export(currentTable, dataTable, dialog.FileName);
            settings.LastExportDirectory = SafeDialog.Normalize(Path.GetDirectoryName(Path.GetFullPath(dialog.FileName)));
            settings.Save();
            SetStatus($"已导出 CSV：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (currentTable == null || !CommitGrid())
            return;
        var dialog = new OpenFileDialog
        {
            Title = "从 CSV 导入",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*"
        };
        if (!SafeDialog.Show(dialog, this, settings.LastExportDirectory))
            return;

        var mode = MessageBox.Show(this,
            "是：清空当前数据，只保留 CSV 中的行（整体替换）\n否：按 ID 合并（已存在的行更新，缺失的行追加）",
            "CSV 导入方式", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (mode == MessageBoxResult.Cancel)
            return;

        try
        {
            var result = CsvIO.Import(currentTable, dataTable, dialog.FileName, replaceAll: mode == MessageBoxResult.Yes);
            AfterBulkImport($"CSV 导入完成：新增 {result.Inserted:N0} 行，更新 {result.Updated:N0} 行" +
                            (result.Skipped > 0 ? $"，跳过 {result.Skipped:N0} 个单元格" : ""));
            ShowWarnings(result.Warnings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportSqlFile_Click(object sender, RoutedEventArgs e)
    {
        if (currentTable == null || !CommitGrid())
            return;

        var modeDialog = new SqlExportDialog { Owner = this };
        if (modeDialog.ShowDialog() != true)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "导出 MySQL SQL 文件",
            Filter = "SQL 文件 (*.sql)|*.sql",
            FileName = currentTable.TableName + ".sql"
        };
        if (!SafeDialog.Show(dialog, this, settings.LastExportDirectory))
            return;

        try
        {
            SqlDump.Write(currentTable, dataTable, dialog.FileName, currentTable.TableName, modeDialog.Options);
            settings.LastExportDirectory = SafeDialog.Normalize(Path.GetDirectoryName(Path.GetFullPath(dialog.FileName)));
            settings.Save();
            SetStatus($"已导出 SQL（{(modeDialog.Options.IncludeDrop ? "完整重建" : "幂等增量")}）：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportSqlFile_Click(object sender, RoutedEventArgs e)
    {
        if (currentTable == null || !CommitGrid())
            return;
        var dialog = new OpenFileDialog
        {
            Title = "从 SQL 文件导入",
            Filter = "SQL 文件 (*.sql)|*.sql|所有文件 (*.*)|*.*"
        };
        if (!SafeDialog.Show(dialog, this, settings.LastExportDirectory))
            return;

        var mode = MessageBox.Show(this,
            "是：清空当前数据，只保留 SQL 中的行（整体替换）\n否：按 ID 合并（幂等，可重复执行）",
            "SQL 导入方式", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (mode == MessageBoxResult.Cancel)
            return;

        try
        {
            var result = SqlScriptImporter.Apply(currentTable, dataTable, dialog.FileName,
                replaceAll: mode == MessageBoxResult.Yes);
            AfterBulkImport($"SQL 导入完成：新增 {result.Inserted:N0} 行，更新 {result.Updated:N0} 行" +
                            (result.Skipped > 0 ? $"，跳过 {result.Skipped:N0} 个单元格" : ""));
            ShowWarnings(result.Warnings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AfterBulkImport(string message)
    {
        undo?.Clear();
        Grid.Items.Refresh();
        ApplyFilter();
        SetDirty(true);
        RefreshSelectedRow();
        SetStatus(message);
    }

    private void ShowWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings == null || warnings.Count == 0)
            return;
        MessageBox.Show(this, string.Join(Environment.NewLine, warnings), "导入提示（部分内容已忽略）",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportMySql_Click(object sender, RoutedEventArgs e)
    {
        if (currentTable == null || !CommitGrid())
            return;
        var dialog = new MySqlDialog(MySqlDialogMode.Export, settings, currentTable, dataTable) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            PersistConnection(dialog.ConnectionInfo);
            SetStatus($"已导出 {dialog.AffectedRows:N0} 行到 MySQL 表");
        }
    }

    private void ImportMySql_Click(object sender, RoutedEventArgs e)
    {
        if (currentTable == null)
            return;
        var dialog = new MySqlDialog(MySqlDialogMode.Import, settings, currentTable, dataTable) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            PersistConnection(dialog.ConnectionInfo);
            undo?.Clear();
            SetDirty(true);
            Grid.Items.Refresh();
            ApplyFilter();
            RefreshSelectedRow();
            SetStatus($"已从 MySQL 导入 {dialog.AffectedRows:N0} 行，请保存为 DBC/DB2");
        }
    }

    private void PersistConnection(MySqlConnectionInfo info)
    {
        if (info == null)
            return;
        settings.MySqlHost = info.Host;
        settings.MySqlPort = info.Port;
        settings.MySqlUser = info.User;
        settings.MySqlDatabase = info.Database;
        settings.Save();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ReloadDefinitions_Click(object sender, RoutedEventArgs e)
    {
        definitions?.InvalidateCache();
        SetStatus("定义缓存已清空，下次打开文件时重新读取");
    }

    private void RebuildRecentMenu()
    {
        MenuRecent.Items.Clear();
        var files = settings.RecentFiles.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            MenuRecent.Items.Add(new MenuItem { Header = "（无）", IsEnabled = false });
            return;
        }
        foreach (var file in files)
        {
            var item = new MenuItem { Header = file, ToolTip = file };
            item.Click += (_, _) =>
            {
                if (!ConfirmDiscardChanges())
                    return;
                OpenPath(file);
            };
            MenuRecent.Items.Add(item);
        }
        MenuRecent.Items.Add(new Separator());
        var clear = new MenuItem { Header = "清空列表" };
        clear.Click += (_, _) =>
        {
            settings.RecentFiles.Clear();
            settings.Save();
            RebuildRecentMenu();
        };
        MenuRecent.Items.Add(clear);
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "基本用法\n" +
            "1) 文件 → 打开，选择 .dbc / .db2 文件（定义从 exe 同级的 definitions 目录读取）\n" +
            "2) 双击单元格编辑；宽表建议用右侧「字段」面板逐字段改，回车提交并跳到下一项\n" +
            "3) Ctrl+S 保存（自动写临时文件再替换，避免写坏原文件）\n" +
            "4) Ctrl+Z / Ctrl+Y 撤销重做，Ctrl+D 复制当前行\n\n" +
            "批量数据\n" +
            "• 导出 CSV：给 Excel 编辑，编辑完用「从 CSV 导入」按 ID 合并回来\n" +
            "• 导出 MySQL SQL 文件：可重复执行（幂等增量默认开启），也可以整体重建\n" +
            "• 从 SQL 文件导入：按主键幂等合并，重复导入不会产生重复行\n" +
            "• MySQL 导出/导入：直连数据库读写整表\n\n" +
            "提示：修改服务端使用的 DBC/DB2 前请先备份。",
            "使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "WoW DBC/DB2 修改器 2.0\n\n基于 WoWDBDefs 定义与 DBCD.IO 读写引擎。\n" +
            "支持 WDBC、WDB2-WDB6、WDC1-WDC5 格式的打开、编辑、保存，\n" +
            "CSV / MySQL SQL 文件 / MySQL 数据库的幂等导入导出。\n\n定义目录：" +
            (definitions?.DefinitionsDirectory ?? "(未配置)"),
            "关于", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges())
        {
            e.Cancel = true;
            return;
        }
        settings.ShowDetailPane = DetailColumn.Width.Value > 1;
        if (DetailColumn.Width.Value > 1)
            settings.DetailPaneWidth = DetailColumn.Width.Value;
        settings.Save();
        undo?.Dispose();
        currentTable?.Dispose();
    }
}

/// <summary>给 InputBindings 用的最小命令实现。</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action<object> execute;
    public RelayCommand(Action<object> execute) => this.execute = execute;
    public bool CanExecute(object parameter) => true;
    public void Execute(object parameter) => execute(parameter);
    public event EventHandler CanExecuteChanged { add { } remove { } }
}
