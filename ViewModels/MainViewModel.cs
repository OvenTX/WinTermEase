using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using WindowsTerminal.Models;
using WindowsTerminal.Services;

namespace WindowsTerminal.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly ConfigService _configService = new();
    public AppConfig Config { get; private set; }

    private TerminalTabViewModel? _activeTab;
    private string _globalStatus = "就绪";

    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = [];
    public ObservableCollection<QuickCommand> QuickCommands { get; } = [];

    private string _selectedGroup = "全部";
    public string SelectedGroup
    {
        get => _selectedGroup;
        set { _selectedGroup = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredQuickCommands)); }
    }

    public IEnumerable<QuickCommand> FilteredQuickCommands =>
        _selectedGroup == "全部" ? QuickCommands : QuickCommands.Where(q => q.Group == _selectedGroup);

    public TerminalTabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            _activeTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => ActiveTab?.StatusText ?? _globalStatus;

    public string RxDisplay => ActiveTab?.RxDisplay ?? "0B";
    public string TxDisplay => ActiveTab?.TxDisplay ?? "0B";

    public ICommand NewSerialCommand { get; }
    public ICommand NewSshCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand AddQuickCommandCommand { get; }
    public ICommand EditQuickCommandCommand { get; }
    public ICommand DeleteQuickCommandCommand { get; }
    public ICommand SendQuickCommandCommand { get; }
    public ICommand SaveConfigCommand { get; }

    public MainViewModel()
    {
        Config = _configService.Load();
        foreach (var qc in Config.QuickCommands)
            QuickCommands.Add(qc);

        NewSerialCommand = new RelayCommand(_ => RequestNewSerial?.Invoke());
        NewSshCommand = new RelayCommand(_ => RequestNewSsh?.Invoke());
        CloseTabCommand = new RelayCommand(tab =>
        {
            if (tab is TerminalTabViewModel t) CloseTab(t);
        });
        AddQuickCommandCommand = new RelayCommand(_ => RequestAddQuickCommand?.Invoke());
        EditQuickCommandCommand = new RelayCommand(qc =>
        {
            if (qc is QuickCommand q) RequestEditQuickCommand?.Invoke(q);
        });
        DeleteQuickCommandCommand = new RelayCommand(qc =>
        {
            if (qc is QuickCommand q) DeleteQuickCommand(q);
        });
        SendQuickCommandCommand = new RelayCommand(qc =>
        {
            if (qc is QuickCommand q) SendQuickCommand(q);
        });
        SaveConfigCommand = new RelayCommand(_ => SaveConfig());

        // Refresh filtered view when commands are added/removed
        QuickCommands.CollectionChanged += (s, e) => OnPropertyChanged(nameof(FilteredQuickCommands));
    }

    // 请求 View 层打开对话框的事件（避免 ViewModel 直接引用 Window）
    public event Action? RequestNewSerial;
    public event Action? RequestNewSsh;
    public event Action? RequestAddQuickCommand;
    public event Action<QuickCommand>? RequestEditQuickCommand;

    public TerminalTabViewModel OpenTab(ConnectionProfile profile)
    {
        var tab = new TerminalTabViewModel(profile);
        tab.StateChanged += () =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(RxDisplay));
                OnPropertyChanged(nameof(TxDisplay));
            });
        };
        tab.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(TerminalTabViewModel.RxDisplay)
                                or nameof(TerminalTabViewModel.TxDisplay))
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    OnPropertyChanged(nameof(RxDisplay));
                    OnPropertyChanged(nameof(TxDisplay));
                });
            }
        };

        Tabs.Add(tab);
        ActiveTab = tab;

        // 保存连接配置
        if (!Config.ConnectionProfiles.Any(p => p.Id == profile.Id))
        {
            Config.ConnectionProfiles.Add(profile);
            SaveConfig();
        }

        // Connect() 由调用方 (MainWindow) 在绑定好 SendToTerminal 事件后手动调用
        return tab;
    }

    public void CloseTab(TerminalTabViewModel tab)
    {
        tab.Disconnect();
        tab.Dispose();
        Tabs.Remove(tab);
        ActiveTab = Tabs.LastOrDefault();
    }

    public void AddOrUpdateQuickCommand(QuickCommand qc)
    {
        var existing = QuickCommands.FirstOrDefault(q => q.Id == qc.Id);
        if (existing != null)
        {
            var idx = QuickCommands.IndexOf(existing);
            QuickCommands[idx] = qc;
        }
        else
        {
            QuickCommands.Add(qc);
        }
        SyncQuickCommandsToConfig();
        SaveConfig();
    }

    private void DeleteQuickCommand(QuickCommand qc)
    {
        QuickCommands.Remove(qc);
        SyncQuickCommandsToConfig();
        SaveConfig();
    }

    private void SendQuickCommand(QuickCommand qc)
    {
        if (ActiveTab?.State != TabState.Connected) return;
        var cmd = qc.AppendNewLine ? qc.Command + "\n" : qc.Command;
        ActiveTab.SendInput(cmd);
    }

    private void SyncQuickCommandsToConfig()
    {
        Config.QuickCommands = [.. QuickCommands];
    }

    public string[] GetAvailableGroups()
    {
        var groups = QuickCommands.Select(q => q.Group).Distinct().OrderBy(g => g).ToArray();
        return ["全部", .. groups];
    }

    public void SaveConfig()
    {
        SyncQuickCommandsToConfig();
        _configService.Save(Config);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
