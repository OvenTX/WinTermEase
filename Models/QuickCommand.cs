namespace WinTermEase.Models;

public class QuickCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Command";
    public string Command { get; set; } = "";
    public string Group { get; set; } = "Default";
    public bool AppendNewLine { get; set; } = true;
}
