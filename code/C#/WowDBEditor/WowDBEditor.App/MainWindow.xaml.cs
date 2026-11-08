using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WowDBEditor.App.Dialogs;
using WowDBEditor.Core;
using WowDBEditor.Core.MySql;

namespace WowDBEditor.App;

public partial class MainWindow : Window
{
    private AppSettings settings;
    private DefinitionService definitions;
    private DbTable currentTable;
    private DataTable dataTable;
    private DataView dataView;
    private string openedBuild;
    private bool isDirty;
    private bool suppressDirty;

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
        InputBindings.Add(new KeyBinding(ApplicationCommands.Open, Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save, Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ApplicationCommands.SaveAs, Key.S, ModifierKeys.Control | ModifierKeys.Shift));

        UpdateUiState();
        UpdateTitle();

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
            Dispatcher.BeginInvoke(new Action(() => OpenPath(args[1])), DispatcherPriority.Loaded);
    }

    private void UpdateUiState()
    {
        var hasTable = currentTable != null;
        BtnAddRow.IsEnabled = hasTable;
        BtnDeleteRow.IsEnabled = hasTable;
        BtnExportMySql.IsEnabled = hasTable;
        BtnImportMySql.IsEnabled = hasTable;
        BtnExportSql.IsEnabled = hasTable;
        MenuExportMySql.IsEnabled = hasTable;
        MenuImportMySql.IsEnabled = hasTable;
        MenuExportSql.IsEnabled = hasTable;
        FilterBox.IsEnabled = hasTable;
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateTitle()
    {
        Title = currentTable == null
            ? "WowDBEditor"
            : $"WowDBEditor — {Path.GetFileName(currentTable.FilePath)}{(isDirty ? " *" : "")}";
        DirtyText.Text = isDirty ? "已修改（未保存）" : "";
    }

    private void UpdateInfoBar()
    {
        if (currentTable == null)
        {
            InfoText.Text = "未打开文件";
            return;
        }
        var version = openedBuild ?? GetVersionLabel();
        InfoText.Text = $"{currentTable.FilePath}    格式: {currentTable.Format}    版本: {version}    " +
                        $"行数: {currentTable.RowCount:N0}    列数: {currentTable.Columns.Count}    Layout: {currentTable.LayoutHash:X8}";
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
        MessageBox.Show(this, "尚未配置有效的定义目录。请选择 WoWDBDefs 项目的 definitions 文件夹。",
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
        if (!string.IsNullOrEmpty(settings.LastOpenDirectory) && Directory.Exists(settings.LastOpenDirectory))
            dialog.InitialDirectory = settings.LastOpenDirectory;
        if (dialog.ShowDialog(this) == true)
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
                var definition = definitions.GetDefinition(ex.TableName);
                var choices = definitions.ListVersionChoices(definition)
                    .Select(c => new BuildPickDialog.Choice(c.Label, c.Build)).ToList();
                if (choices.Count == 0)
                    throw new InvalidOperationException($"定义文件 {ex.TableName}.dbd 中没有可用的版本。", ex);
                var picker = new BuildPickDialog(choices,
                    $"无法自动匹配 {Path.GetFileName(path)} 的定义版本，请手动选择对应的客户端版本：") { Owner = this };
                if (picker.ShowDialog() != true)
                    return;
                build = picker.SelectedBuild;
                table = DbTable.Open(path, definitions, build);
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

        LoadTable(table, build);
        settings.LastOpenDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        settings.Save();
        SetStatus($"已打开 {Path.GetFileName(path)}（{table.RowCount:N0} 行）");
    }

    private void LoadTable(DbTable table, string build)
    {
        suppressDirty = true;
        try
        {
            if (dataView != null)
                dataView.ListChanged -= OnListChanged;
            currentTable?.Dispose();

            currentTable = table;
            openedBuild = build;
            dataTable = table.CreateDataTable();
            dataView = new DataView(dataTable);
            dataView.ListChanged += OnListChanged;
            Grid.ItemsSource = dataView;
            BuildGridColumns();
            FilterBox.Text = "";
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
    }

    private void BuildGridColumns()
    {
        Grid.Columns.Clear();
        foreach (var column in currentTable.Columns)
        {
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
                    ? (System.Windows.Media.Brush)FindResource("Foreground")
                    : (System.Windows.Media.Brush)FindResource("MutedForeground")
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
            Grid.Columns.Add(gridColumn);
        }
        Grid.FrozenColumnCount = currentTable.IdColumn != null ? 1 : 0;
    }

    private static string BuildColumnTooltip(DbColumn column)
    {
        var tip = $"{column.BaseName}    类型: {column.TypeLabel}";
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
            var total = dataTable.AsEnumerable().Count(r => r.RowState != DataRowState.Deleted);
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
            if (dialog.ShowDialog(this) != true)
                return;
            path = dialog.FileName;
        }

        suppressDirty = true;
        try
        {
            currentTable.Save(dataTable, path);
            isDirty = false;
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

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        if (currentTable == null || !CommitGrid())
            return;

        FilterBox.Text = "";
        var row = dataTable.NewRow();
        foreach (var column in currentTable.Columns)
            row[column.Key] = column.FieldType == typeof(string) ? "" : Activator.CreateInstance(column.FieldType);

        if (currentTable.IdColumn != null)
        {
            var maxId = dataTable.AsEnumerable()
                .Where(r => r.RowState != DataRowState.Deleted)
                .Select(r => Convert.ToInt32(r[currentTable.IdColumn.Key], CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0)
                .Max();
            row[currentTable.IdColumn.Key] = maxId + 1;
        }

        dataTable.Rows.Add(row);
        var view = dataView.Cast<DataRowView>().FirstOrDefault(v => ReferenceEquals(v.Row, row));
        if (view != null)
        {
            Grid.SelectedItem = view;
            Grid.ScrollIntoView(view);
        }
        SetStatus("已添加新行");
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
            row.Delete();
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
            SetDirty(true);
            Grid.Items.Refresh();
            ApplyFilter();
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

    private void ExportSqlFile_Click(object sender, RoutedEventArgs e)
    {
        if (currentTable == null || !CommitGrid())
            return;
        var dialog = new SaveFileDialog
        {
            Title = "导出 SQL 文件",
            Filter = "SQL 文件 (*.sql)|*.sql",
            FileName = currentTable.TableName + ".sql"
        };
        if (!string.IsNullOrEmpty(settings.LastExportDirectory) && Directory.Exists(settings.LastExportDirectory))
            dialog.InitialDirectory = settings.LastExportDirectory;
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            SqlDump.Write(currentTable, dataTable, dialog.FileName, currentTable.TableName, includeDrop: true);
            settings.LastExportDirectory = Path.GetDirectoryName(Path.GetFullPath(dialog.FileName));
            settings.Save();
            SetStatus($"已导出 SQL：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ReloadDefinitions_Click(object sender, RoutedEventArgs e)
    {
        definitions?.InvalidateCache();
        SetStatus("定义缓存已清空，下次打开文件时重新读取");
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "WowDBEditor 1.0\n\n基于 WoWDBDefs 定义与 DBCD.IO 读写引擎。\n" +
            "支持 WDBC、WDB2-WDB6、WDC1-WDC5 格式的打开、编辑、保存，\n以及与 MySQL 数据库的互相导入导出。\n\n定义目录：" +
            (definitions?.DefinitionsDirectory ?? "(未配置)"),
            "关于 WowDBEditor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges())
        {
            e.Cancel = true;
            return;
        }
        settings.Save();
        currentTable?.Dispose();
    }
}