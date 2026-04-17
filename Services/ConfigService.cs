using System.IO;
using System.Text.Json;
using WinTermEase.Models;

namespace WinTermEase.Services;

public class ConfigService
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTermEase");
    private static readonly string LegacyConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsTerminal");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
    private static readonly string LegacyConfigPath = Path.Combine(LegacyConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppConfig Load()
    {
        try
        {
            var path = File.Exists(ConfigPath)
                ? ConfigPath
                : File.Exists(LegacyConfigPath)
                    ? LegacyConfigPath
                    : null;
            if (path == null) return CreateDefault();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    private static AppConfig CreateDefault() => new()
    {
        QuickCommands =
        [
            new QuickCommand { Name = "换行", Command = "", AppendNewLine = true },
            new QuickCommand { Name = "Ctrl+C", Command = "\x03", AppendNewLine = false },
            new QuickCommand { Name = "清屏", Command = "clear", AppendNewLine = true },
        ]
    };
}
