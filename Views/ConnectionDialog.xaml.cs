using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinTermEase.Models;
using WinTermEase.Services;

namespace WinTermEase.Views;

public partial class ConnectionDialog : Window
{
    public ConnectionProfile? Result { get; private set; }

    private static readonly int[] BaudRates =
        [300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600, 1000000, 2000000];

    // When loading from history this holds the original profile ID so we update instead of duplicate.
    private string? _selectedProfileId;

    public ConnectionDialog(ConnectionType initialType = ConnectionType.Serial,
                            IReadOnlyList<ConnectionProfile>? recentProfiles = null)
    {
        InitializeComponent();

        // Populate COM ports
        CbPort.ItemsSource = SerialPortService.GetPortNames();
        if (CbPort.Items.Count > 0) CbPort.SelectedIndex = 0;

        // Populate baud rates and select 115200 as default
        CbBaud.ItemsSource = BaudRates;
        CbBaud.Text = "115200";

        CbDataBits.SelectedIndex = 0;
        CbParity.SelectedIndex = 0;
        CbStopBits.SelectedIndex = 0;
        CbHandshake.SelectedIndex = 0;

        if (initialType == ConnectionType.SSH)
        {
            RbSsh.IsChecked = true;
            RbSerial.IsChecked = false;
        }

        // Populate history ComboBox — show all types, most recent first
        var recent = recentProfiles?
            .Where(p => p.LastConnectedAt.HasValue)
            .OrderByDescending(p => p.LastConnectedAt)
            .ToList() ?? [];

        if (recent.Count > 0)
        {
            CbRecent.ItemsSource = recent;
        }
        else
        {
            TxtHistoryHint.Visibility = Visibility.Visible;
            CbRecent.IsEnabled = false;
        }
    }

    private void CbRecent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbRecent.SelectedItem is not ConnectionProfile p) return;

        _selectedProfileId = p.Id;
        TxtName.Text = p.Name;

        if (p.Type == ConnectionType.Serial)
        {
            RbSerial.IsChecked = true;
            CbPort.Text = p.PortName;
            CbBaud.Text = p.BaudRate.ToString();
            SelectComboByContent(CbDataBits, p.DataBits.ToString());
            SelectComboByContent(CbParity, p.Parity);
            SelectComboByContent(CbStopBits, p.StopBits);
            SelectComboByContent(CbHandshake, p.Handshake);
        }
        else
        {
            RbSsh.IsChecked = true;
            TxtHost.Text = p.Host;
            TxtSshPort.Text = p.Port.ToString();
            TxtUser.Text = p.Username;
            PbPassword.Password = p.Password;
            ChkUseKey.IsChecked = p.UsePrivateKey;
            TxtKeyPath.Text = p.PrivateKeyPath;
        }
    }

    private static void SelectComboByContent(ComboBox cb, string value)
    {
        foreach (ComboBoxItem item in cb.Items)
        {
            if (item.Content?.ToString() == value)
            {
                cb.SelectedItem = item;
                return;
            }
        }
    }

    private void ConnType_Changed(object sender, RoutedEventArgs e)
    {
        if (PanelSerial == null) return;
        PanelSerial.Visibility = RbSerial.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelSsh.Visibility = RbSsh.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChkUseKey_Checked(object sender, RoutedEventArgs e)
    {
        bool useKey = ChkUseKey.IsChecked == true;
        PbPassword.IsEnabled = !useKey;
        TxtKeyPath.IsEnabled = useKey;
        BtnBrowseKey.IsEnabled = useKey;
    }

    private void BtnBrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "私钥文件|*.pem;*.ppk;*.key|所有文件|*.*" };
        if (dlg.ShowDialog(this) == true)
            TxtKeyPath.Text = dlg.FileName;
    }

    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (RbSerial.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(CbPort.Text))
            {
                MessageBox.Show("请选择串口", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(CbBaud.Text, out var baud) || baud <= 0)
            {
                MessageBox.Show("请输入有效的波特率", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Result = new ConnectionProfile
            {
                Id = _selectedProfileId ?? Guid.NewGuid().ToString(),
                Name = TxtName.Text.Trim().Length > 0 ? TxtName.Text.Trim() : CbPort.Text,
                Type = ConnectionType.Serial,
                PortName = CbPort.Text.Trim(),
                BaudRate = baud,
                DataBits = int.Parse(((ComboBoxItem)CbDataBits.SelectedItem).Content.ToString()!),
                Parity = ((ComboBoxItem)CbParity.SelectedItem).Content.ToString()!,
                StopBits = ((ComboBoxItem)CbStopBits.SelectedItem).Content.ToString()!,
                Handshake = ((ComboBoxItem)CbHandshake.SelectedItem).Content.ToString()!,
            };
        }
        else
        {
            if (string.IsNullOrWhiteSpace(TxtHost.Text))
            {
                MessageBox.Show("请输入主机地址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Result = new ConnectionProfile
            {
                Id = _selectedProfileId ?? Guid.NewGuid().ToString(),
                Name = TxtName.Text.Trim().Length > 0 ? TxtName.Text.Trim() : $"{TxtUser.Text}@{TxtHost.Text}",
                Type = ConnectionType.SSH,
                Host = TxtHost.Text.Trim(),
                Port = int.TryParse(TxtSshPort.Text, out var p) ? p : 22,
                Username = TxtUser.Text.Trim(),
                Password = PbPassword.Password,
                UsePrivateKey = ChkUseKey.IsChecked == true,
                PrivateKeyPath = TxtKeyPath.Text.Trim(),
            };
        }
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
