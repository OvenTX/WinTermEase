using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using WindowsTerminal.Models;
using WindowsTerminal.Services;

namespace WindowsTerminal.ViewModels;

public enum TabState { Disconnected, Connecting, Connected, Error }

public class TerminalTabViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SerialPortService? _serial;
    private readonly SshService? _ssh;

    // Patterns for inline keyword coloring in serial output
    private static readonly Regex _errorRegex = new(
        @"(?i)\b(error|exception|fail(ed|ure)?|fault|critical|fatal)\b",
        RegexOptions.Compiled);
    private static readonly Regex _warnRegex = new(
        @"(?i)\b(warn(ing)?)\b",
        RegexOptions.Compiled);

    private string _title = "New Tab";
    private TabState _state = TabState.Disconnected;
    private string _statusText = "未连接";
    private long _rxBytes;
    private long _txBytes;

    // Accumulated raw text for log saving (pre-ANSI, from session start)
    private readonly StringBuilder _logBuffer = new();

    public string Id { get; } = Guid.NewGuid().ToString();
    public ConnectionProfile Profile { get; }
    public ConnectionType ConnectionType => Profile.Type;

    // 当有数据需要写入 xterm.js 时触发（base64 编码）
    public event Action<string>? SendToTerminal;
    // 状态变化通知（用于 UI 刷新状态栏）
    public event Action? StateChanged;

    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); }
    }

    public TabState State
    {
        get => _state;
        private set { _state = value; OnPropertyChanged(); StateChanged?.Invoke(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public long RxBytes
    {
        get => _rxBytes;
        private set { _rxBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(RxDisplay)); }
    }

    public long TxBytes
    {
        get => _txBytes;
        private set { _txBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(TxDisplay)); }
    }

    public string RxDisplay => FormatBytes(RxBytes);
    public string TxDisplay => FormatBytes(TxBytes);

    public TerminalTabViewModel(ConnectionProfile profile)
    {
        Profile = profile;
        Title = profile.Name;

        if (profile.Type == ConnectionType.Serial)
        {
            _serial = new SerialPortService();
            _serial.DataReceived += OnDataReceived;
            _serial.Error += OnError;
            _serial.Disconnected += OnDisconnected;
        }
        else
        {
            _ssh = new SshService();
            _ssh.DataReceived += OnDataReceived;
            _ssh.Error += OnError;
            _ssh.Disconnected += OnDisconnected;
        }
    }

    public void Connect()
    {
        SetState(TabState.Connecting, "连接中...");
        Task.Run(() =>
        {
            try
            {
                if (Profile.Type == ConnectionType.Serial)
                {
                    _serial!.Open(Profile);
                    var status = BuildStatusText();
                    SetState(TabState.Connected, status);
                }
                else
                {
                    _ssh!.Connect(Profile);
                    var status = BuildStatusText();
                    SetState(TabState.Connected, status);
                }
            }
            catch (Exception ex)
            {
                SetState(TabState.Error, $"错误: {ex.Message}");
            }
        });
    }

    public void Disconnect()
    {
        _serial?.Close();
        _ssh?.Disconnect();
        State = TabState.Disconnected;
        StatusText = "已断开";
    }

    /// <summary>用户在 xterm.js 中输入的内容，转发给串口/SSH</summary>
    public void SendInput(string text)
    {
        try
        {
            if (Profile.Type == ConnectionType.Serial)
                _serial?.Write(text);
            else
                _ssh?.Write(text);

            TxBytes = Profile.Type == ConnectionType.Serial
                ? _serial?.TxBytes ?? 0
                : _ssh?.TxBytes ?? 0;
        }
        catch (Exception ex)
        {
            OnError(ex.Message);
        }
    }

    /// <summary>通知 SSH 终端尺寸变化</summary>
    public void Resize(int cols, int rows) => _ssh?.Resize(cols, rows);

    private void OnDataReceived(byte[] data)
    {
        // Accumulate raw text in log buffer (before any ANSI injection)
        _logBuffer.Append(Profile.Type == ConnectionType.Serial
            ? Encoding.Latin1.GetString(data)
            : Encoding.UTF8.GetString(data));

        var processed = Profile.Type == ConnectionType.Serial ? HighlightSerialData(data) : data;
        var b64 = Convert.ToBase64String(processed);
        SendToTerminal?.Invoke(b64);

        // UI 属性更新必须在 UI 线程
        Application.Current?.Dispatcher.Invoke(() =>
        {
            RxBytes = Profile.Type == ConnectionType.Serial
                ? _serial?.RxBytes ?? 0
                : _ssh?.RxBytes ?? 0;
        });
    }

    /// <summary>Returns all received text since the tab was opened (raw, no ANSI codes).</summary>
    public string GetLogContent() => _logBuffer.ToString();

    /// <summary>
    /// Injects ANSI color codes around error/warning keywords in serial output.
    /// Skips data that already contains ANSI escape sequences.
    /// </summary>
    private byte[] HighlightSerialData(byte[] data)
    {
        // Use Latin1 to safely round-trip arbitrary bytes
        var text = Encoding.Latin1.GetString(data);

        // Don't double-color if the device already sends ANSI codes
        if (text.Contains('\x1b')) return data;

        text = _errorRegex.Replace(text, m => $"\x1b[31m{m.Value}\x1b[0m");
        text = _warnRegex.Replace(text, m => $"\x1b[33m{m.Value}\x1b[0m");

        return Encoding.Latin1.GetBytes(text);
    }

    private void OnError(string msg)
    {
        SetState(TabState.Error, $"错误: {msg}");
        var errMsg = $"\r\n\x1b[31m[错误] {msg}\x1b[0m\r\n";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(errMsg));
        SendToTerminal?.Invoke(b64);
    }

    private void OnDisconnected()
    {
        SetState(TabState.Disconnected, "连接已断开");
        var msg = "\r\n\x1b[33m[连接已断开]\x1b[0m\r\n";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(msg));
        SendToTerminal?.Invoke(b64);
    }

    /// <summary>线程安全地设置状态和文字，总是派发到 UI 线程</summary>
    private void SetState(TabState state, string statusText)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            Application.Current.Dispatcher.Invoke(() => SetState(state, statusText));
            return;
        }
        State = state;
        StatusText = statusText;
    }

    private string BuildStatusText()
    {
        if (Profile.Type == ConnectionType.Serial)
            return $"{Profile.PortName}  {Profile.BaudRate}  {Profile.DataBits}{Profile.Parity[0]}{Profile.StopBits.Replace("One", "1").Replace("Two", "2")}";
        return $"{Profile.Username}@{Profile.Host}:{Profile.Port}";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        _ => $"{bytes / (1024.0 * 1024):F1}MB"
    };

    public void Dispose()
    {
        _serial?.Dispose();
        _ssh?.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
