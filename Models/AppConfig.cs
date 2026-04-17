namespace WinTermEase.Models;

public class AppConfig
{
    public List<ConnectionProfile> ConnectionProfiles { get; set; } = [];
    public List<QuickCommand> QuickCommands { get; set; } = [];
    public string Theme { get; set; } = "dark";
    public string FontFamily { get; set; } = "Cascadia Code, Consolas, monospace";
    public int FontSize { get; set; } = 14;
    public int ScrollbackLines { get; set; } = 5000;
}
