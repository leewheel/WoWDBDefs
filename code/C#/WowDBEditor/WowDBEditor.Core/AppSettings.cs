using System.Text.Json;

namespace WowDBEditor.Core;

public sealed class AppSettings
{
    public string DefinitionsDirectory { get; set; }
    public string LastOpenDirectory { get; set; }
    public string LastExportDirectory { get; set; }
    public string MySqlHost { get; set; } = "127.0.0.1";
    public int MySqlPort { get; set; } = 3306;
    public string MySqlUser { get; set; } = "root";
    public string MySqlDatabase { get; set; } = "wowdbeditor";
    public List<string> RecentFiles { get; set; } = new();
    public bool ShowDetailPane { get; set; } = true;
    public double DetailPaneWidth { get; set; } = 380;
    /// <summary>表名 → 上次手动选择的 BUILD，老格式（WDBC）没有 LayoutHash 时靠它免去重复选择。</summary>
    public Dictionary<string, string> BuildOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        path = Path.GetFullPath(path);
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        while (RecentFiles.Count > 10)
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WowDBEditor", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            // 损坏的设置文件按默认值处理
        }
        return new AppSettings();
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}