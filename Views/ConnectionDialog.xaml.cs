using System.Windows;
using Microsoft.Win32;
using WindowsTerminal.Models;
using WindowsTerminal.Services;

namespace WindowsTerminal.Views;

public partial class ConnectionDialog : Window
{
    public ConnectionProfile? Result { get; private set; }

    private static readonly int[] BaudRates =
        [300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600, 1000000, 2000000];

    public ConnectionDialog(ConnectionType initialType = ConnectionType.Serial)
    {
        InitializeComponent();

        // Populate COM ports
        CbPort.ItemsSource = SerialPortService.GetPortNames();
        if (CbPort.Items.Count > 0) CbPort.SelectedIndex = 0;

        // Populate baud rates and select 115200 as default
        CbBaud.ItemsSource = BaudRates;
        CbBaud.Text = "115200";

        // Set defaults for other combos
        CbDataBits.SelectedIndex = 0;
        CbParity.SelectedIndex = 0;
        CbStopBits.SelectedIndex = 0;
        CbHandshake.SelectedIndex = 0;

        if (initialType == ConnectionType.SSH)
        {
            RbSsh.IsChecked = true;
            RbSerial.IsChecked = false;
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
            // 直接从 CbBaud.Text 读取波特率（兼容下拉选择和手动输入两种情况）
            if (!int.TryParse(CbBaud.Text, out var baud) || baud <= 0)
            {
                MessageBox.Show("请输入有效的波特率", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Result = new ConnectionProfile
            {
                Name = TxtName.Text.Trim().Length > 0 ? TxtName.Text.Trim() : CbPort.Text,
                Type = ConnectionType.Serial,
                PortName = CbPort.Text.Trim(),
                BaudRate = baud,
                DataBits = int.Parse(((System.Windows.Controls.ComboBoxItem)CbDataBits.SelectedItem).Content.ToString()!),
                Parity = ((System.Windows.Controls.ComboBoxItem)CbParity.SelectedItem).Content.ToString()!,
                StopBits = ((System.Windows.Controls.ComboBoxItem)CbStopBits.SelectedItem).Content.ToString()!,
                Handshake = ((System.Windows.Controls.ComboBoxItem)CbHandshake.SelectedItem).Content.ToString()!,
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
