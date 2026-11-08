using System.Data;
using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using MySqlConnector;
using WowDBEditor.Core;

var repo = @"G:\WoWTools\DBReader\WoWDBDefs";
var exe = Path.Combine(repo, @"code\C#\WowDBEditor\WowDBEditor.App\bin\Release\net10.0-windows\WowDBEditor.exe");
var sample = @"G:\MYWOW\MYWOW735\data\dbc\zhcn\Map.db2";
var shots = @"C:\Users\leewheel\AppData\Local\Temp\wowdbeditor-ui";
Directory.CreateDirectory(shots);
var workCopy = Path.Combine(shots, "Map.db2");
File.Copy(sample, workCopy, true);

var definitions = new DefinitionService(Path.Combine(repo, "definitions"));
int directoryIndex;
int originalRows;
using (var probe = DbTable.Open(workCopy, definitions))
{
    directoryIndex = probe.Columns.ToList().FindIndex(c => c.Key == "Directory");
    originalRows = probe.RowCount;
}
Console.WriteLine($"sample: rows={originalRows}, Directory column index={directoryIndex}");

var failures = 0;
Application app = null;
UIA3Automation automation = null;
Window window = null;

void Step(string name, Action action)
{
    try
    {
        if (window != null)
        {
            window.SetForeground();
            Thread.Sleep(250);
        }
        action();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL  {name}: {ex}");
        try { if (window != null) Capture.Element(window).ToFile(Path.Combine(shots, "failure.png")); } catch { }
    }
}

AutomationElement Find(string id) => window.FindFirstDescendant(cf => cf.ByAutomationId(id));
string FindText(string id) => Find(id).Properties.Name.Value ?? "";
void ClickButton(string id) => Find(id).AsButton().Invoke();
void SetText(AutomationElement scope, string id, string text) =>
    scope.FindFirstDescendant(cf => cf.ByAutomationId(id)).AsTextBox().Text = text;

Window FindDialog(string title) => Retry.WhileNull(
    () => app.GetAllTopLevelWindows(automation).FirstOrDefault(w => w.Title == title)
          ?? automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(title).And(cf.ByControlType(ControlType.Window)))?.AsWindow(),
    TimeSpan.FromSeconds(15)).Result;

bool DialogExists(string title) =>
    app.GetAllTopLevelWindows(automation).Any(w => w.Title == title)
    || automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(title).And(cf.ByControlType(ControlType.Window))) != null;

void ConfirmBox(string title)
{
    var box = Retry.WhileNull(
        () => app.GetAllTopLevelWindows(automation).FirstOrDefault(w => w.Title == title)
              ?? automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(title).And(cf.ByControlType(ControlType.Window))),
        TimeSpan.FromSeconds(10)).Result;
    if (box == null) throw new Exception($"未找到确认窗口 [{title}]");
    box.SetForeground();
    box.FindFirstDescendant(cf => cf.ByName("确定")).AsButton().Invoke();
    Thread.Sleep(400);
}

try
{
    var psi = new ProcessStartInfo(exe)
    {
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(exe)
    };
    psi.ArgumentList.Add(workCopy);
    app = Application.Launch(psi);
    automation = new UIA3Automation();
    window = app.GetMainWindow(automation, TimeSpan.FromSeconds(30));
    if (window == null) throw new Exception("主窗口未出现");

    Step("启动并自动打开文件", () =>
    {
        Retry.WhileFalse(() => window.Title.Contains("Map.db2"), TimeSpan.FromSeconds(30));
        Retry.WhileFalse(() => FindText("InfoText").Contains("768"), TimeSpan.FromSeconds(15));
        window.Focus();
        Capture.Element(window).ToFile(Path.Combine(shots, "01-main.png"));
    });

    Step("文本筛选", () =>
    {
        Find("FilterBox").AsTextBox().Text = "Azeroth";
        Thread.Sleep(600);
        var match = FindText("MatchCount");
        var shown = int.Parse(match.Split('/')[0].Trim().Replace(",", ""));
        if (shown < 1 || shown >= originalRows)
            throw new Exception($"筛选结果异常: {match}");
        Capture.Element(window).ToFile(Path.Combine(shots, "02-filter.png"));
        Find("FilterBox").AsTextBox().Text = "";
        Thread.Sleep(400);
    });

    Step("编辑单元格并保存", () =>
    {
        Find("Grid").Focus();
        window.SetForeground();
        Thread.Sleep(300);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.HOME);
        for (var i = 0; i < directoryIndex; i++)
            Keyboard.Press(VirtualKeyShort.RIGHT);
        Keyboard.Press(VirtualKeyShort.F2);
        Keyboard.Type("UITEST_DIR");
        Keyboard.Press(VirtualKeyShort.ENTER);
        Thread.Sleep(400);
        Retry.WhileFalse(() => window.Title.EndsWith("*"), TimeSpan.FromSeconds(5));
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S);
        Retry.WhileFalse(() => !window.Title.EndsWith("*"), TimeSpan.FromSeconds(15));
        using var check = DbTable.Open(workCopy, definitions);
        var data = check.CreateDataTable();
        var actual = (string)data.Rows[0]["Directory"];
        if (actual != "UITEST_DIR")
        {
            using var original = DbTable.Open(sample, definitions);
            var originalData = original.CreateDataTable();
            var origIndex = originalData.AsEnumerable().ToDictionary(r => r[original.IdColumn.Key]);
            foreach (DataRow row in data.Rows)
            {
                var other = origIndex[row[check.IdColumn.Key]];
                foreach (var column in check.Columns)
                    if (!Equals(row[column.Key], other[column.Key]))
                        Console.WriteLine($"  changed cell: ID={row[check.IdColumn.Key]} col={column.Key} [{other[column.Key]}] -> [{row[column.Key]}]");
            }
            throw new Exception($"保存后回读不一致: [{actual}]");
        }
    });

    Step("添加行", () =>
    {
        ClickButton("BtnAddRow");
        Thread.Sleep(500);
        var match = FindText("MatchCount");
        if (!match.Contains($"{originalRows + 1} / {originalRows + 1}"))
            throw new Exception($"添加行后计数异常: {match}");
    });

    Step("删除行", () =>
    {
        ClickButton("BtnDeleteRow");
        ConfirmBox("删除行");
        Thread.Sleep(500);
        var match = FindText("MatchCount");
        if (!match.Contains($"{originalRows} / {originalRows}"))
            throw new Exception($"删除行后计数异常: {match}");
    });

    Step("MySQL 导出对话框", () =>
    {
        ClickButton("BtnExportMySql");
        var dialog = FindDialog("导出到 MySQL");
        if (dialog == null) throw new Exception("导出对话框未出现");
        dialog.SetForeground();
        SetText(dialog, "HostBox", "127.0.0.1");
        SetText(dialog, "PortBox", "3307");
        SetText(dialog, "UserBox", "root");
        SetText(dialog, "DatabaseBox", "wowdbeditor_ui");
        SetText(dialog, "TableBox", "Map");
        dialog.FindFirstDescendant(cf => cf.ByAutomationId("TestButton")).AsButton().Invoke();
        Retry.WhileFalse(
            () => (dialog.FindFirstDescendant(cf => cf.ByAutomationId("StatusText")).Properties.Name.Value ?? "").Contains("连接成功"),
            TimeSpan.FromSeconds(15));
        Capture.Element(dialog).ToFile(Path.Combine(shots, "03-mysql-export.png"));
        dialog.FindFirstDescendant(cf => cf.ByAutomationId("ActionButton")).AsButton().Invoke();
        Retry.WhileTrue(() => DialogExists("导出到 MySQL"), TimeSpan.FromSeconds(120));
        Retry.WhileFalse(() => FindText("StatusText").Contains("已导出"), TimeSpan.FromSeconds(10));
    });

    Step("MySQL 导出结果核对", () =>
    {
        using var connection = new MySqlConnection("Server=127.0.0.1;Port=3307;User ID=root;Password=;Database=wowdbeditor_ui;SslMode=Preferred");
        connection.Open();
        using var command = new MySqlCommand("SELECT COUNT(*) FROM Map", connection);
        var count = Convert.ToInt32(command.ExecuteScalar());
        if (count != originalRows) throw new Exception($"MySQL 行数 {count} != {originalRows}");
        command.CommandText = "SELECT Directory FROM Map WHERE ID = 0";
        var dir = (string)command.ExecuteScalar();
        if (dir != "UITEST_DIR") throw new Exception($"MySQL 中编辑值丢失: [{dir}]");
    });

    Step("MySQL 导入替换数据", () =>
    {
        ClickButton("BtnAddRow");
        Thread.Sleep(400);
        ClickButton("BtnImportMySql");
        var dialog = FindDialog("从 MySQL 导入");
        if (dialog == null) throw new Exception("导入对话框未出现");
        dialog.SetForeground();
        SetText(dialog, "HostBox", "127.0.0.1");
        SetText(dialog, "PortBox", "3307");
        SetText(dialog, "UserBox", "root");
        SetText(dialog, "DatabaseBox", "wowdbeditor_ui");
        Capture.Element(dialog).ToFile(Path.Combine(shots, "04-mysql-import.png"));
        dialog.FindFirstDescendant(cf => cf.ByAutomationId("ActionButton")).AsButton().Invoke();
        ConfirmBox("确认导入");
        Retry.WhileTrue(() => DialogExists("从 MySQL 导入"), TimeSpan.FromSeconds(120));
        Retry.WhileFalse(() => FindText("StatusText").Contains("已从 MySQL 导入"), TimeSpan.FromSeconds(10));
        Thread.Sleep(400);
        var match = FindText("MatchCount");
        if (!match.Contains($"{originalRows} / {originalRows}"))
            throw new Exception($"导入后计数异常: {match}");
    });

    Step("收尾保存与截图", () =>
    {
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S);
        Retry.WhileFalse(() => !window.Title.EndsWith("*"), TimeSpan.FromSeconds(15));
        Capture.Element(window).ToFile(Path.Combine(shots, "05-final.png"));
    });

    Step("MySQL 导入后文件核对", () =>
    {
        using var check = DbTable.Open(workCopy, definitions);
        if (check.RowCount != originalRows)
            throw new Exception($"最终文件行数 {check.RowCount} != {originalRows}");
        var data = check.CreateDataTable();
        if ((string)data.Rows[0]["Directory"] != "UITEST_DIR")
            throw new Exception("最终文件编辑值丢失");
    });
}
catch (Exception ex)
{
    failures++;
    Console.WriteLine("FATAL: " + ex);
}
finally
{
    try
    {
        if (window != null) window.Close();
        Thread.Sleep(1200);
        if (app is { HasExited: false }) app.Kill();
    }
    catch { }
    automation?.Dispose();
    app?.Dispose();
    try
    {
        using var connection = new MySqlConnection("Server=127.0.0.1;Port=3307;User ID=root;Password=;SslMode=Preferred");
        connection.Open();
        using var command = new MySqlCommand("DROP DATABASE IF EXISTS wowdbeditor_ui", connection);
        command.ExecuteNonQuery();
    }
    catch { }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "UI ALL PASS" : $"{failures} UI FAILURE(S)");
Console.WriteLine("screenshots: " + shots);
return failures;