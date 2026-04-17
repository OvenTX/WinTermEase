using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;
using WinTermEase.Models;

namespace WinTermEase.Services;

/// <summary>
/// 高性能串口服务：独立读取线程 + 双缓冲队列 + 16ms 批量刷新到终端
/// </summary>
public class SerialPortService : IDisposable
{
    private SerialPort? _port;
    private Thread? _readThread;
    private System.Threading.Timer? _flushTimer;
    private readonly ConcurrentQueue<byte[]> _receiveQueue = new();
    private volatile bool _running;

    // 每 16ms 将队列中的数据合并后推送给终端（约 60fps）
    private const int FlushIntervalMs = 16;
    // 单次推送最大字节数，防止一帧数据过大卡渲染
    private const int MaxFlushBytes = 65536;

    public event Action<byte[]>? DataReceived;
    public event Action<string>? Error;
    public event Action? Disconnected;

    public bool IsOpen => _port?.IsOpen == true;
    public string PortName => _port?.PortName ?? "";
    public long RxBytes { get; private set; }
    public long TxBytes { get; private set; }

    public void Open(ConnectionProfile profile)
    {
        Close();

        _port = new SerialPort
        {
            PortName  = profile.PortName,
            BaudRate  = profile.BaudRate,
            DataBits  = profile.DataBits,
            Parity    = Enum.Parse<Parity>(profile.Parity),
            StopBits  = Enum.Parse<StopBits>(profile.StopBits),
            Handshake = Enum.Parse<Handshake>(profile.Handshake),
            ReadBufferSize  = 1024 * 1024,
            WriteBufferSize = 256 * 1024,
            ReadTimeout  = SerialPort.InfiniteTimeout,
            WriteTimeout = 3000,
        };

        _port.Open();
        _running = true;

        // 独立高优先级读线程
        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = $"SerialRead-{profile.PortName}"
        };
        _readThread.Start();

        // 刷新定时器
        _flushTimer = new System.Threading.Timer(_ => Flush(), null,
            FlushIntervalMs, FlushIntervalMs);
    }

    public void Close()
    {
        _running = false;
        _flushTimer?.Dispose();
        _flushTimer = null;

        try { _port?.Close(); } catch { }
        _port?.Dispose();
        _port = null;

        _readThread = null;
        // 清空队列
        while (_receiveQueue.TryDequeue(out _)) { }
    }

    public void Write(string text)
    {
        if (_port?.IsOpen != true) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _port.Write(bytes, 0, bytes.Length);
            TxBytes += bytes.Length;
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
    }

    public void WriteBytes(byte[] data)
    {
        if (_port?.IsOpen != true) return;
        try
        {
            _port.Write(data, 0, data.Length);
            TxBytes += data.Length;
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
    }

    private void ReadLoop()
    {
        var buf = new byte[65536];
        while (_running && _port?.IsOpen == true)
        {
            try
            {
                int n = _port.Read(buf, 0, buf.Length);
                if (n > 0)
                {
                    var chunk = new byte[n];
                    Buffer.BlockCopy(buf, 0, chunk, 0, n);
                    _receiveQueue.Enqueue(chunk);
                    RxBytes += n;
                }
            }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                if (_running)
                {
                    Error?.Invoke(ex.Message);
                    Disconnected?.Invoke();
                    _running = false;
                }
            }
        }
    }

    private void Flush()
    {
        if (_receiveQueue.IsEmpty) return;

        // 将队列中所有 chunk 合并成一个 byte[]，限制最大大小
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

    public static string[] GetPortNames() => SerialPort.GetPortNames();

    public void Dispose() => Close();
}
