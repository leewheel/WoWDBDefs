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