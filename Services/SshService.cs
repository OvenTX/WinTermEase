using System.Collections.Concurrent;
using System.Text;
using Renci.SshNet;
using WindowsTerminal.Models;

namespace WindowsTerminal.Services;

/// <summary>
/// SSH 服务：SSH.NET ShellStream + 独立读取线程 + 16ms 批量刷新
/// </summary>
public class SshService : IDisposable
{
    private SshClient? _client;
    private ShellStream? _stream;
    private Thread? _readThread;
    private System.Threading.Timer? _flushTimer;
    private readonly ConcurrentQueue<byte[]> _receiveQueue = new();
    private volatile bool _running;

    private const int FlushIntervalMs = 16;
    private const int MaxFlushBytes = 65536;

    public event Action<byte[]>? DataReceived;
    public event Action<string>? Error;
    public event Action? Disconnected;

    public bool IsConnected => _client?.IsConnected == true;
    public long RxBytes { get; private set; }
    public long TxBytes { get; private set; }

    // Terminal dimensions（连接时设定，Resize 时更新）
    private int _cols = 220;
    private int _rows = 50;

    public void Connect(ConnectionProfile profile)
    {
        Disconnect();

        AuthenticationMethod auth = profile.UsePrivateKey && !string.IsNullOrEmpty(profile.PrivateKeyPath)
            ? new PrivateKeyAuthenticationMethod(profile.Username,
                new PrivateKeyFile(profile.PrivateKeyPath))
            : new PasswordAuthenticationMethod(profile.Username, profile.Password);

        var connInfo = new ConnectionInfo(profile.Host, profile.Port, profile.Username, auth);
        _client = new SshClient(connInfo);
        _client.Connect();

        _stream = _client.CreateShellStream(
            terminalName: "xterm-256color",
            columns: (uint)_cols,
            rows: (uint)_rows,
            width: 0,
            height: 0,
            bufferSize: 1024 * 1024);

        _running = true;

        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = $"SshRead-{profile.Host}"
        };
        _readThread.Start();

        _flushTimer = new System.Threading.Timer(_ => Flush(), null,
            FlushIntervalMs, FlushIntervalMs);
    }

    public void Disconnect()
    {
        _running = false;
        _flushTimer?.Dispose();
        _flushTimer = null;

        try { _stream?.Close(); } catch { }
        _stream?.Dispose();
        _stream = null;

        try { _client?.Disconnect(); } catch { }
        _client?.Dispose();
        _client = null;

        while (_receiveQueue.TryDequeue(out _)) { }
    }

    public void Write(string text)
    {
        if (_stream == null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
            TxBytes += bytes.Length;
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
    }

    /// <summary>
    /// 通知服务端终端尺寸变化（SSH "window-change" 请求）
    /// </summary>
    public void Resize(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        // SSH.NET 的 ShellStream 不直接暴露 window-change，
        // 需要通过底层 Channel 发送。此处预留入口，后续可扩展。
        // 当前 workaround：重建连接（生产环境建议用 Renci.SshNet 内部 API）
    }

    private void ReadLoop()
    {
        var buf = new byte[65536];
        while (_running && _stream != null)
        {
            try
            {
                int n = _stream.Read(buf, 0, buf.Length);
                if (n > 0)
                {
                    var chunk = new byte[n];
                    Buffer.BlockCopy(buf, 0, chunk, 0, n);
                    _receiveQueue.Enqueue(chunk);
                    RxBytes += n;
                }
                else
                {
                    // 连接已关闭
                    if (_running)
                    {
                        Disconnected?.Invoke();
                        _running = false;
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    Error?.Invoke(ex.Message);
                    Disconnected?.Invoke();
                    _running = false;
                }
                break;
            }
        }
    }

    private void Flush()
    {
        if (_receiveQueue.IsEmpty) return;

        var segments = new List<byte[]>();
        int total = 0;
        while (total < MaxFlushBytes && _receiveQueue.TryDequeue(out var chunk))
        {
            segments.Add(chunk);
            total += chunk.Length;
        }
        if (segments.Count == 0) return;

        byte[] merged;
        if (segments.Count == 1)
        {
            merged = segments[0];
        }
        else
        {
            merged = new byte[total];
            int offset = 0;
            foreach (var seg in segments)
            {
                Buffer.BlockCopy(seg, 0, merged, offset, seg.Length);
                offset += seg.Length;
            }
        }

        DataReceived?.Invoke(merged);
    }

    public void Dispose() => Disconnect();
}
