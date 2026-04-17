using System.Windows;
using WinTermEase.Models;

namespace WinTermEase.Views;

public partial class QuickCommandEditDialog : Window
{
    public QuickCommand? Result { get; private set; }

    public QuickCommandEditDialog(QuickCommand? existing = null)
    {
        InitializeComponent();

        if (existing != null)
        {
            TxtName.Text = existing.Name;
            TxtCommand.Text = existing.Command;
            TxtGroup.Text = existing.Group;
            ChkNewLine.IsChecked = existing.AppendNewLine;
            Result = existing; // preserve Id
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtName.Text))
        {
            MessageBox.Show("请输入指令名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new QuickCommand
        {
            Id = Result?.Id ?? Guid.NewGuid().ToString(),
            Name = TxtName.Text.Trim(),
            Command = TxtCommand.Text,
            Group = TxtGroup.Text.Trim().Length > 0 ? TxtGroup.Text.Trim() : "Default",
            AppendNewLine = ChkNewLine.IsChecked == true,
        };
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
