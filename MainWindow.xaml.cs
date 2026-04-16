using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using WindowsTerminal.Converters;
using WindowsTerminal.Models;
using WindowsTerminal.ViewModels;
using WindowsTerminal.Views;

namespace WindowsTerminal;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    private readonly Dictionary<string, WebView2> _webViews = [];
    private readonly Dictionary<string, Button> _tabButtons = [];

    private static readonly string AssetsDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");

    // Quick command overflow state
    private readonly List<QuickCommand> _overflowCmds = [];
    private readonly Dictionary<Button, double> _btnWidths = new();
    private Popup? _overflowPopup;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.RequestNewSerial += ShowNewSerialDialog;
        _vm.RequestNewSsh += ShowNewSshDialog;
        _vm.RequestAddQuickCommand += ShowAddQuickCommandDialog;
        _vm.RequestEditQuickCommand += ShowEditQuickCommandDialog;

        // Refresh group buttons + command strip whenever the filtered set changes
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == "FilteredQuickCommands")
            {
                Dispatcher.Invoke(RefreshGroupButtons);
                Dispatcher.Invoke(RefreshQuickCommandButtons);
            }
        };

        RefreshGroupButtons();
        RefreshQuickCommandButtons();

        NoTabHint.Visibility = Visibility.Visible;

        // Sync toggle button icon with the current theme
        BtnToggleTheme.Content = _vm.Config.Theme == "light" ? "🌙" : "☀";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TAB MANAGEMENT
    // ─────────────────────────────────────────────────────────────────────────

    private async void OpenNewTab(ConnectionProfile profile)
    {
        var tabVm = _vm.OpenTab(profile);

        var wv = new WebView2
        {
            Visibility = Visibility.Collapsed,
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 30, 30, 30),
        };
        TerminalArea.Children.Add(wv);

        try
        {
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

        wv.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "terminal.local", AssetsDir,
            CoreWebView2HostResourceAccessKind.Allow);

        wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        wv.CoreWebView2.Settings.IsStatusBarEnabled = false;

        wv.CoreWebView2.WebMessageReceived += (s, e) =>
        {
            try
            {
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
                    case "paste_request":
                        Dispatcher.Invoke(() =>
                        {
                            var text = Clipboard.GetText();
                            if (string.IsNullOrEmpty(text)) return;

                            // Count non-empty lines
                            var lines = text.Split('\n');
                            int lineCount = lines.Count(l => l.TrimEnd('\r').Length > 0);

                            if (lineCount > 1)
                            {
                                // Build preview: first 3 lines, truncate long lines
                                var preview = string.Join("\n", lines.Take(3)
                                    .Select(l => l.TrimEnd('\r'))
                                    .Select(l => l.Length > 60 ? l[..60] + "…" : l));
                                if (lines.Length > 3) preview += "\n…";

                                var result = MessageBox.Show(
                                    $"即将粘贴 {lineCount} 行内容：\n\n{preview}\n\n确认粘贴？",
                                    "多行粘贴确认",
                                    MessageBoxButton.YesNo,
                                    MessageBoxImage.Question,
                                    MessageBoxResult.No);

                                if (result != MessageBoxResult.Yes) return;
                            }

                            tabVm.SendInput(text);
                        });
                        break;
                    case "ready":
                        if (tabVm.State == TabState.Disconnected)
                            tabVm.Connect();
                        break;
                }
            }
            catch { }
        };

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
        wv.Source = new Uri("https://terminal.local/terminal.html");
        CreateTabHeader(tabVm);
        ActivateTab(tabVm);
    }

    private void CreateTabHeader(TerminalTabViewModel tabVm)
    {
        var icon = tabVm.ConnectionType == ConnectionType.Serial ? "⚡" : "🔒";

        var label = new TextBlock
        {
            Text = $"{icon} {tabVm.Title}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            MaxWidth = 140,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        tabVm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(TerminalTabViewModel.Title))
                Dispatcher.Invoke(() => label.Text = $"{icon} {tabVm.Title}");
            if (e.PropertyName == nameof(TerminalTabViewModel.State))
                Dispatcher.Invoke(() =>
                    label.Foreground = tabVm.State switch
                    {
                        TabState.Connected   => (SolidColorBrush)Application.Current.Resources["TabConnectedFgBrush"],
                        TabState.Connecting  => (SolidColorBrush)Application.Current.Resources["TabConnectingFgBrush"],
                        TabState.Error       => (SolidColorBrush)Application.Current.Resources["TabErrorFgBrush"],
                        _                    => (SolidColorBrush)Application.Current.Resources["TabDisconnectedFgBrush"]
                    });
        };

        var closeBtn = new Button
        {
            Content = "✕",
            Width = 20, Height = 20,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeBtn.SetResourceReference(Button.ForegroundProperty, "AppSubtleFgBrush");
        closeBtn.Click += (s, e) => { e.Handled = true; CloseTab(tabVm); };

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

        foreach (var (id, wv) in _webViews)
            wv.Visibility = id == tabVm.Id ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (id, btn) in _tabButtons)
            btn.Style = id == tabVm.Id
                ? (Style)Resources["ActiveTabHeaderButton"]
                : (Style)Resources["TabHeaderButton"];

        NoTabHint.Visibility = Visibility.Collapsed;

        if (_webViews.TryGetValue(tabVm.Id, out var active))
            active.Focus();
    }

    private void CloseTab(TerminalTabViewModel tabVm)
    {
        if (_webViews.TryGetValue(tabVm.Id, out var wv))
        {
            TerminalArea.Children.Remove(wv);
            wv.Dispose();
            _webViews.Remove(tabVm.Id);
        }

        if (_tabButtons.TryGetValue(tabVm.Id, out var btn))
        {
            TabStrip.Children.Remove(btn);
            _tabButtons.Remove(tabVm.Id);
        }

        _vm.CloseTab(tabVm);

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
        var dlg = new ConnectionDialog(ConnectionType.Serial, _vm.Config.ConnectionProfiles) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
            OpenNewTab(dlg.Result);
    }

    private void ShowNewSshDialog()
    {
        var dlg = new ConnectionDialog(ConnectionType.SSH, _vm.Config.ConnectionProfiles) { Owner = this };
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
            var g = group;
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
    // QUICK COMMAND STRIP (code-behind, supports overflow "···" button)
    // ─────────────────────────────────────────────────────────────────────────

    private void RefreshQuickCommandButtons()
    {
        QuickCmdStrip.Children.Clear();
        _overflowCmds.Clear();
        BtnMoreCmds.Visibility = Visibility.Collapsed;

        var conv = (CommandDisplayConverter)Resources["CmdDisplayConverter"];

        foreach (var qc in _vm.FilteredQuickCommands)
        {
            var qcRef = qc;
            var btn = new Button
            {
                Content = qc.Name,
                Style = (Style)Resources["QuickCmdButton"],
                ToolTip = conv.Convert(qc.Command, typeof(string), null!,
                              System.Globalization.CultureInfo.CurrentCulture),
                Tag = qc,
            };
            btn.Click += (s, e) => _vm.SendQuickCommandCommand.Execute(qcRef);

            // Per-button context menu
            var cm = new ContextMenu();
            var miSend   = new MenuItem { Header = "发送",  Tag = qcRef };
            var miEdit   = new MenuItem { Header = "编辑",  Tag = qcRef };
            var miDelete = new MenuItem { Header = "删除",  Tag = qcRef };
            miSend.Click   += QuickCmd_Send_Click;
            miEdit.Click   += QuickCmd_Edit_Click;
            miDelete.Click += QuickCmd_Delete_Click;
            cm.Items.Add(miSend);
            cm.Items.Add(miEdit);
            cm.Items.Add(new Separator());
            cm.Items.Add(miDelete);
            btn.ContextMenu = cm;

            QuickCmdStrip.Children.Add(btn);
        }

        // Measure rendered widths after layout, then check overflow
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            (Action)ReadWidthsThenCheckOverflow);
    }

    private void ReadWidthsThenCheckOverflow()
    {
        // Make all buttons visible so WPF measures their true widths
        var buttons = QuickCmdStrip.Children.OfType<Button>().ToList();
        foreach (var b in buttons) b.Visibility = Visibility.Visible;

        // Read ActualWidth for each button now that they're rendered
        _btnWidths.Clear();
        foreach (var b in buttons)
            _btnWidths[b] = b.ActualWidth;

        CheckQuickCmdOverflow();
    }

    private void CheckQuickCmdOverflow()
    {
        double containerW = QuickCmdContainerGrid.ActualWidth;
        if (containerW <= 0) return;

        const double moreBtnW = 32; // reserved width for the "···" button

        var buttons = QuickCmdStrip.Children.OfType<Button>().ToList();

        double totalW = buttons.Sum(b => _btnWidths.GetValueOrDefault(b, 0));

        if (totalW <= containerW)
        {
            // All fit — show everything, hide "···"
            foreach (var b in buttons) b.Visibility = Visibility.Visible;
            BtnMoreCmds.Visibility = Visibility.Collapsed;
            _overflowCmds.Clear();
            return;
        }

        // Some overflow — fit as many as possible, reserve space for "···"
        _overflowCmds.Clear();
        double usedW = 0;
        bool overflowing = false;

        for (int i = 0; i < buttons.Count; i++)
        {
            double w = _btnWidths.GetValueOrDefault(buttons[i], 0);
            if (!overflowing && usedW + w + moreBtnW <= containerW)
            {
                buttons[i].Visibility = Visibility.Visible;
                usedW += w;
            }
            else
            {
                overflowing = true;
                buttons[i].Visibility = Visibility.Collapsed;
                if (buttons[i].Tag is QuickCommand qc)
                    _overflowCmds.Add(qc);
            }
        }

        BtnMoreCmds.Visibility = Visibility.Visible;
    }

    private void QuickCmdArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        CheckQuickCmdOverflow();
    }

    private void BtnMoreCmds_Click(object sender, RoutedEventArgs e)
    {
        if (_overflowPopup?.IsOpen == true)
        {
            _overflowPopup.IsOpen = false;
            return;
        }

        var conv = (CommandDisplayConverter)Resources["CmdDisplayConverter"];
        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4),
            MaxWidth = 420,
        };

        foreach (var qc in _overflowCmds)
        {
            var qcRef = qc;
            var btn = new Button
            {
                Content = qc.Name,
                Style = (Style)Resources["QuickCmdButton"],
                ToolTip = conv.Convert(qc.Command, typeof(string), null!,
                              System.Globalization.CultureInfo.CurrentCulture),
                Tag = qc,
            };
            btn.Click += (s, ev) =>
            {
                _vm.SendQuickCommandCommand.Execute(qcRef);
                _overflowPopup!.IsOpen = false;
            };

            var cm = new ContextMenu();
            var miSend   = new MenuItem { Header = "发送",  Tag = qcRef };
            var miEdit   = new MenuItem { Header = "编辑",  Tag = qcRef };
            var miDelete = new MenuItem { Header = "删除",  Tag = qcRef };
            miSend.Click   += QuickCmd_Send_Click;
            miEdit.Click   += (s, ev) => { ShowEditQuickCommandDialog(qcRef); _overflowPopup!.IsOpen = false; };
            miDelete.Click += QuickCmd_Delete_Click;
            cm.Items.Add(miSend);
            cm.Items.Add(miEdit);
            cm.Items.Add(new Separator());
            cm.Items.Add(miDelete);
            btn.ContextMenu = cm;

            wrap.Children.Add(btn);
        }

        _overflowPopup = new Popup
        {
            Child = new Border
            {
                Background      = (SolidColorBrush)Application.Current.Resources["AppPanelBrush"],
                BorderBrush     = (SolidColorBrush)Application.Current.Resources["AppInputBorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Child = new ScrollViewer
                {
                    MaxHeight = 280,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                    Content = wrap,
                },
            },
            PlacementTarget  = BtnMoreCmds,
            Placement        = PlacementMode.Top,
            StaysOpen        = false,
            AllowsTransparency = true,
            PopupAnimation   = PopupAnimation.Slide,
        };
        _overflowPopup.IsOpen = true;
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
    // SAVE LOG
    // ─────────────────────────────────────────────────────────────────────────

    private void BtnSaveLog_Click(object sender, RoutedEventArgs e)
    {
        var tab = _vm.ActiveTab;
        if (tab == null)
        {
            MessageBox.Show("没有活动的终端窗口。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var content = tab.GetLogContent();
        if (string.IsNullOrEmpty(content))
        {
            MessageBox.Show("当前窗口暂无可保存的内容。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title       = "保存终端日志",
            FileName    = $"{tab.Title}_{DateTime.Now:yyyyMMdd_HHmmss}.log",
            Filter      = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件|*.*",
            DefaultExt  = "log",
            AddExtension = true,
        };

        if (dlg.ShowDialog(this) == true)
        {
            File.WriteAllText(dlg.FileName, content, Encoding.UTF8);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // THEME TOGGLE
    // ─────────────────────────────────────────────────────────────────────────

    private void BtnToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        var newTheme = _vm.Config.Theme == "light" ? "dark" : "light";
        _vm.Config.Theme = newTheme;
        App.ApplyTheme(newTheme);
        BtnToggleTheme.Content = newTheme == "light" ? "🌙" : "☀";
        _vm.SaveConfig();
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
