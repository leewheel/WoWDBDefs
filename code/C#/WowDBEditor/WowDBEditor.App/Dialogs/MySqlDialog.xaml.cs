using System.Data;
using System.Windows;
using WowDBEditor.Core;
using WowDBEditor.Core.MySql;

namespace WowDBEditor.App.Dialogs;

public enum MySqlDialogMode
{
    Export,
    Import
}

public partial class MySqlDialog : Window
{
    private readonly MySqlDialogMode mode;
    private readonly DbTable table;
    private readonly DataTable data;
    private CancellationTokenSource cts;
    private bool running;

    public MySqlConnectionInfo ConnectionInfo { get; private set; }
    public int AffectedRows { get; private set; }

    public MySqlDialog(MySqlDialogMode mode, AppSettings settings, DbTable table, DataTable data)
    {
        InitializeComponent();
        this.mode = mode;
        this.table = table;
        this.data = data;

        HostBox.Text = settings.MySqlHost;
        PortBox.Text = settings.MySqlPort.ToString();
        UserBox.Text = settings.MySqlUser;
        DatabaseBox.Text = settings.MySqlDatabase;

        if (mode == MySqlDialogMode.Export)
        {
            Title = "导出到 MySQL";
            HeaderText.Text = "导出当前表格到 MySQL";
            ActionButton.Content = "导出";
            TableBox.Text = table.TableName;
            HintText.Text = "数据库不存在时会自动创建。密码仅用于本次连接，不会被保存。";
        }
        else
        {
            Title = "从 MySQL 导入";
            HeaderText.Text = "从 MySQL 表导入到当前表格";
            ActionButton.Content = "导入";
            TableBox.Visibility = Visibility.Collapsed;
            TableCombo.Visibility = Visibility.Visible;
            RefreshTablesButton.Visibility = Visibility.Visible;
            TableCombo.Text = table.TableName;
            ReplaceCheck.Visibility = Visibility.Collapsed;
            HintText.Text = "MySQL 表必须包含当前文件的全部列（列名一致）。导入会替换当前编辑的全部数据，完成后请保存为 DBC/DB2。";
        }
    }

    private MySqlConnectionInfo ReadConnection()
    {
        var host = HostBox.Text.Trim();
        var user = UserBox.Text.Trim();
        var database = DatabaseBox.Text.Trim();
        if (host.Length == 0 || user.Length == 0 || database.Length == 0)
            throw new ArgumentException("主机、用户名、数据库均不能为空。");
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port < 1 || port > 65535)
            throw new ArgumentException("端口必须是 1-65535 之间的整数。");
        return new MySqlConnectionInfo(host, port, user, PasswordBox.Password, database);
    }

    private string ReadTableName()
    {
        var name = (mode == MySqlDialogMode.Export ? TableBox.Text : TableCombo.Text).Trim();
        if (name.Length == 0)
            throw new ArgumentException("表名不能为空。");
        return name;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetRunning(true);
            StatusText.Text = "正在连接…";
            await MySqlService.TestConnectionAsync(ReadConnection(), cts.Token);
            StatusText.Text = "连接成功。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            MessageBox.Show(this, ex.Message, "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetRunning(false);
        }
    }

    private async void RefreshTables_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetRunning(true);
            StatusText.Text = "正在读取表列表…";
            var tables = await MySqlService.ListTablesAsync(ReadConnection(), cts.Token);
            var current = TableCombo.Text;
            TableCombo.ItemsSource = tables;
            TableCombo.Text = current;
            StatusText.Text = $"共 {tables.Count} 张表。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            MessageBox.Show(this, ex.Message, "读取失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetRunning(false);
        }
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        MySqlConnectionInfo info;
        string tableName;
        try
        {
            info = ReadConnection();
            tableName = ReadTableName();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (mode == MySqlDialogMode.Import)
        {
            var confirm = MessageBox.Show(this, "导入将替换当前表格中的全部数据，是否继续？", "确认导入",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
                return;
        }

        try
        {
            SetRunning(true);
            Progress.Visibility = Visibility.Visible;
            Progress.Value = 0;
            var progress = new Progress<TransferProgress>(p =>
            {
                Progress.Value = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
                StatusText.Text = mode == MySqlDialogMode.Export
                    ? $"已写入 {p.Done}/{p.Total} 行…"
                    : $"已读取 {p.Done}/{p.Total} 行…";
            });

            if (mode == MySqlDialogMode.Export)
            {
                StatusText.Text = "正在导出…";
                await MySqlService.ExportAsync(table, data, info, tableName, ReplaceCheck.IsChecked == true, progress, cts.Token);
                AffectedRows = data.AsEnumerable().Count(r => r.RowState != DataRowState.Deleted);
            }
            else
            {
                StatusText.Text = "正在导入…";
                AffectedRows = await MySqlService.ImportAsync(data, table, info, tableName, progress, cts.Token);
            }

            ConnectionInfo = info;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            MessageBox.Show(this, ex.Message, mode == MySqlDialogMode.Export ? "导出失败" : "导入失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            SetRunning(false);
        }
    }

    private void SetRunning(bool value)
    {
        running = value;
        if (value)
            cts = new CancellationTokenSource();
        HostBox.IsEnabled = !value;
        PortBox.IsEnabled = !value;
        UserBox.IsEnabled = !value;
        PasswordBox.IsEnabled = !value;
        DatabaseBox.IsEnabled = !value;
        TableBox.IsEnabled = !value;
        TableCombo.IsEnabled = !value;
        ReplaceCheck.IsEnabled = !value;
        TestButton.IsEnabled = !value;
        RefreshTablesButton.IsEnabled = !value;
        ActionButton.IsEnabled = !value;
        CloseButton.Content = value ? "取消" : "关闭";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (running)
        {
            cts.Cancel();
            StatusText.Text = "正在取消…";
            return;
        }
        DialogResult = false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (running)
        {
            cts.Cancel();
            StatusText.Text = "正在取消…";
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}