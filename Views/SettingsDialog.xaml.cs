using System.Windows;
using System.Windows.Media;
using WinTermEase.Models;

namespace WinTermEase.Views;

public partial class SettingsDialog : Window
{
    public const int MinFontSize = 8;
    public const int MaxFontSize = 32;

    public string FontFamilyText { get; private set; } = "";
    public int FontSizeValue { get; private set; }

    public SettingsDialog(AppConfig config)
    {
        InitializeComponent();

        // 系统已安装字体，按名称排序；可手输 CSS 字体串（如 "Cascadia Code, Consolas, monospace"）
        foreach (var family in Fonts.SystemFontFamilies.OrderBy(f => f.Source))
            CbFontFamily.Items.Add(family.Source);
        CbFontFamily.Text = config.FontFamily;

        for (int size = MinFontSize; size <= MaxFontSize; size++)
            CbFontSize.Items.Add(size.ToString());
        CbFontSize.Text = config.FontSize.ToString();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var family = CbFontFamily.Text.Trim();
        if (string.IsNullOrEmpty(family))
        {
            MessageBox.Show("字体不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(CbFontSize.Text.Trim(), out var size))
        {
            MessageBox.Show("字号必须是数字。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        size = Math.Clamp(size, MinFontSize, MaxFontSize);

        FontFamilyText = family;
        FontSizeValue = size;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
