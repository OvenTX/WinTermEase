using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WindowsTerminal.Models;
using WindowsTerminal.ViewModels;
using WindowsTerminal.Views;

namespace WindowsTerminal;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    // Maps tab ID → its WebView2 control
    private readonly Dictionary<string, WebView2> _webViews = [];
    // Maps tab ID → its tab header button
    private readonly Dictionary<string, Button> _tabButtons = [];

    private static readonly string AssetsDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // Wire up ViewModel events
        _vm.RequestNewSerial += ShowNewSerialDialog;
        _vm.RequestNewSsh += ShowNewSshDialog;
        _vm.RequestAddQuickCommand += ShowAddQuickCommandDialog;
        _vm.RequestEditQuickCommand += ShowEditQuickCommandDialog;

        // Refresh group filter buttons whenever quick commands change
        _vm.QuickCommands.CollectionChanged += (s, e) => RefreshGroupButtons();
        RefreshGroupButtons();

        // Initial hint visibility
        NoTabHint.Visibility = Visibility.Visible;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TAB MANAGEMENT
    // ─────────────────────────────────────────────────────────────────────────

    private async void OpenNewTab(ConnectionProfile profile)
    {
        var tabVm = _vm.OpenTab(profile);

        // Create WebView2
        var wv = new WebView2 { Visibility = Visibility.Collapsed };
        TerminalArea.Children.Add(wv);

        try
        {
            // Initialize WebView2 environment
            var env = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             "WindowsTerminal", "WebView2Cache"));
            await wv.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 初始化失败:\n{ex.Message}\n\n请确认已安装 Microsoft Edge WebView2 Runtime。",
                "初始化错误", MessageBoxButton.OK, MessageBoxImage.Error);
            TerminalArea.Children.Remove(wv);
            _vm.CloseTab(tabVm);
            return;
        }

        // Serve local assets via virtual host
        wv.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "terminal.local", AssetsDir,
            CoreWebView2HostResourceAccessKind.Allow);

        // Suppress context menu in terminal
        wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        wv.CoreWebView2.Settings.IsStatusBarEnabled = false;

        // Wire up messages: xterm.js → C#
        wv.CoreWebView2.WebMessageReceived += (s, e) =>
        {
            try
            {
                // JS 用 postMessage(obj) 发送时，WebMessageAsJson 就是对象的 JSON 表示
                var msg = JsonSerializer.Deserialize<JsonElement>(e.WebMessageAsJson);
                var type = msg.GetProperty("type").GetString();
                switch (type)
                {
                    case "input":
                        var data = msg.GetProperty("data").GetString() ?? "";
                        tabVm.SendInput(data);
                        break;
                    case "resize":
                        var cols = msg.GetProperty("cols").GetInt32();
                        var rows = msg.GetProperty("rows").GetInt32();
                        tabVm.Resize(cols, rows);
                        break;
                    case "ready":
                        // xterm.js 完成初始化并注册了 message 监听器，现在连接才安全
                        // 此时 SendToTerminal 已绑定，不会丢失数据
                        if (tabVm.State == TabState.Disconnected)
                            tabVm.Connect();
                        break;
                }
            }
            catch { /* ignore malformed messages */ }
        };

        // Wire up: C# → xterm.js (data from serial/SSH)
        // 必须在 Connect() 之前绑定，否则连接建立后收到的早期数据会因 SendToTerminal==null 被丢弃
        tabVm.SendToTerminal += b64 =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_webViews.TryGetValue(tabVm.Id, out var webView)
                    && webView.CoreWebView2 != null)
                {
                    var json = $"{{\"type\":\"data\",\"payload\":\"{b64}\"}}";
                    webView.CoreWebView2.PostWebMessageAsString(json);
                }
            });
        };

        _webViews[tabVm.Id] = wv;

        // 加载终端页面（加载完成后 xterm.js 发送 "ready" 消息触发 Connect）
        wv.Source = new Uri("https://terminal.local/terminal.html");

        // 创建标签头
        CreateTabHeader(tabVm);

        // 激活标签（显示 WebView2，焦点到终端）
        ActivateTab(tabVm);
    }

    private void CreateTabHeader(TerminalTabViewModel tabVm)
    {
        // Container: tab label + close button
        var icon = tabVm.ConnectionType == ConnectionType.Serial ? "⚡" : "🔒";

        var label = new TextBlock
        {
            Text = $"{icon} {tabVm.Title}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            MaxWidth = 140,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // Update label when title/state changes
        tabVm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(TerminalTabViewModel.Title))
                Dispatcher.Invoke(() => label.Text = $"{icon} {tabVm.Title}");
            if (e.PropertyName == nameof(TerminalTabViewModel.State))
                Dispatcher.Invoke(() =>
                    label.Foreground = tabVm.State switch
                    {
                        TabState.Connected => Brushes.White,
                        TabState.Connecting => Brushes.Yellow,
                        TabState.Error => Brushes.OrangeRed,
                        _ => new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99))
                    });
        };

        var closeBtn = new Button
        {
            Content = "✕",
            Width = 20, Height = 20,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeBtn.Click += (s, e) =>
        {
            e.Handled = true;
            CloseTab(tabVm);
        };

        var headerContent = new StackPanel { Orientation = Orientation.Horizontal };
        headerContent.Children.Add(label);
        headerContent.Children.Add(closeBtn);

        var tabBtn = new Button
        {
            Content = headerContent,
            Style = (Style)Resources["TabHeaderButton"],
            Tag = tabVm.Id,
        };
        tabBtn.Click += (s, e) => ActivateTab(tabVm);

        _tabButtons[tabVm.Id] = tabBtn;
        TabStrip.Children.Add(tabBtn);
    }

    private void ActivateTab(TerminalTabViewModel tabVm)
    {
        _vm.ActiveTab = tabVm;

        // Show/hide WebView2 instances
        foreach (var (id, wv) in _webViews)
            wv.Visibility = id == tabVm.Id ? Visibility.Visible : Visibility.Collapsed;

        // Update tab button styles
        foreach (var (id, btn) in _tabButtons)
            btn.Style = id == tabVm.Id
                ? (Style)Resources["ActiveTabHeaderButton"]
                : (Style)Resources["TabHeaderButton"];

        NoTabHint.Visibility = Visibility.Collapsed;

        // Focus the WebView2 so keyboard input works immediately
        if (_webViews.TryGetValue(tabVm.Id, out var active))
            active.Focus();
    }

    private void CloseTab(TerminalTabViewModel tabVm)
    {
        // Remove WebView2
        if (_webViews.TryGetValue(tabVm.Id, out var wv))
        {
            TerminalArea.Children.Remove(wv);
            wv.Dispose();
            _webViews.Remove(tabVm.Id);
        }

        // Remove header
        if (_tabButtons.TryGetValue(tabVm.Id, out var btn))
        {
            TabStrip.Children.Remove(btn);
            _tabButtons.Remove(tabVm.Id);
        }

        _vm.CloseTab(tabVm);

        // Activate last remaining tab
        if (_vm.Tabs.Count > 0)
            ActivateTab(_vm.Tabs[^1]);
        else
            NoTabHint.Visibility = Visibility.Visible;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DIALOG HANDLERS
    // ─────────────────────────────────────────────────────────────────────────

    private void ShowNewSerialDialog()
    {
        var dlg = new ConnectionDialog(ConnectionType.Serial) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
            OpenNewTab(dlg.Result);
    }

    private void ShowNewSshDialog()
    {
        var dlg = new ConnectionDialog(ConnectionType.SSH) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
            OpenNewTab(dlg.Result);
    }

    private void ShowAddQuickCommandDialog()
    {
        var dlg = new QuickCommandEditDialog { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
            _vm.AddOrUpdateQuickCommand(dlg.Result);
    }

    private void ShowEditQuickCommandDialog(QuickCommand qc)
    {
        var dlg = new QuickCommandEditDialog(qc) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
            _vm.AddOrUpdateQuickCommand(dlg.Result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GROUP FILTER BUTTONS
    // ─────────────────────────────────────────────────────────────────────────

    private void RefreshGroupButtons()
    {
        GroupFilterPanel.Children.Clear();
        foreach (var group in _vm.GetAvailableGroups())
        {
            var g = group; // capture for lambda
            var btn = new Button
            {
                Content = g,
                Style = g == _vm.SelectedGroup
                    ? (Style)Resources["ActiveGroupFilterButton"]
                    : (Style)Resources["GroupFilterButton"],
            };
            btn.Click += (s, e) =>
            {
                _vm.SelectedGroup = g;
                RefreshGroupButtons();
            };
            GroupFilterPanel.Children.Add(btn);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QUICK COMMAND CONTEXT MENU
    // ─────────────────────────────────────────────────────────────────────────

    private void QuickCmd_Send_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is QuickCommand qc)
            _vm.SendQuickCommandCommand.Execute(qc);
    }

    private void QuickCmd_Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is QuickCommand qc)
            ShowEditQuickCommandDialog(qc);
    }

    private void QuickCmd_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is QuickCommand qc)
        {
            if (MessageBox.Show($"删除快捷指令「{qc.Name}」?", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _vm.DeleteQuickCommandCommand.Execute(qc);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QUICK COMMAND BAR TOGGLE
    // ─────────────────────────────────────────────────────────────────────────

    private void BtnToggleQuickBar_Click(object sender, RoutedEventArgs e)
    {
        QuickCommandBar.Visibility =
            QuickCommandBar.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        BtnToggleQuickBar.Content = QuickCommandBar.Visibility == Visibility.Visible
            ? "快捷栏 ▾" : "快捷栏 ▸";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WINDOW EVENTS
    // ─────────────────────────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        foreach (var tabVm in _vm.Tabs.ToList())
            CloseTab(tabVm);
        _vm.SaveConfig();
        base.OnClosed(e);
    }
}
