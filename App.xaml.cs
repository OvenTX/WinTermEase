using System.Windows;
using WindowsTerminal.Services;

namespace WindowsTerminal;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = new ConfigService().Load();
        ApplyTheme(config.Theme);

        DispatcherUnhandledException += (s, ex) =>
        {
            MessageBox.Show($"未处理的异常:\n{ex.Exception.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
    }

    /// <summary>Swaps the active theme ResourceDictionary. Safe to call from the UI thread.</summary>
    public static void ApplyTheme(string theme)
    {
        var file = theme == "light" ? "Light" : "Dark";
        var uri  = new Uri($"Themes/{file}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        var merged = Current.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d =>
            d.Source?.OriginalString.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase) == true);
        if (existing != null) merged.Remove(existing);
        merged.Add(dict);
    }
}
