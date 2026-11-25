using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetMonitor;

/// <summary>
/// ETW (Event Tracing for Windows) üzerinden "Microsoft-Windows-Kernel-Network"
/// sağlayıcısını dinler ve süreç başına gönderilen/alınan toplam bayt sayısını tutar.
/// Yönetici yetkisi gerektirir.
/// </summary>
public sealed class TrafficMonitor : IDisposable
{
    private const string SessionName = "NetMonitorSession";

    private sealed class Counters
    {
        public long Sent;
        public long Received;
    }

    private readonly ConcurrentDictionary<int, Counters> _counters = new();
    private TraceEventSession? _session;

    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    public void Start()
    {
        try
        {
            if (TraceEventSession.IsElevated() != true)
            {
                Error = "Hız ölçümü için yönetici yetkisi gerekli.";
                return;
            }

            // Önceki çalıştırmadan kalmış bir oturum varsa kapat.
            try { TraceEventSession.GetActiveSession(SessionName)?.Stop(); } catch { }

            var session = new TraceEventSession(SessionName);
            session.EnableProvider("Microsoft-Windows-Kernel-Network");
            session.Source.Dynamic.All += OnEvent;
            _session = session;

            Task.Run(() => session.Source.Process());
            IsRunning = true;
        }
        catch (Exception ex)
        {
            Error = "ETW başlatılamadı: " + ex.Message;
        }
    }

    private void OnEvent(TraceEvent e)
    {
        // Microsoft-Windows-Kernel-Network olay numaraları:
        // 10/11 = TCP IPv4 gönder/al, 26/27 = TCP IPv6, 42/43 = UDP IPv4, 58/59 = UDP IPv6
        int id = (int)e.ID;
        bool isSend = id is 10 or 26 or 42 or 58;
        bool isRecv = id is 11 or 27 or 43 or 59;
        if (!isSend && !isRecv) return;

        object? pidObj = e.PayloadByName("PID");
        object? sizeObj = e.PayloadByName("size");
        if (pidObj == null || sizeObj == null) return;

        int pid = Convert.ToInt32(pidObj);
        long size = Convert.ToInt64(sizeObj);

        var c = _counters.GetOrAdd(pid, _ => new Counters());
        if (isSend) Interlocked.Add(ref c.Sent, size);
        else Interlocked.Add(ref c.Received, size);
    }

    /// <summary>Her sürecin şu ana kadarki toplam gönderilen/alınan baytı.</summary>
    public Dictionary<int, (long Sent, long Received)> Snapshot()
    {
        var result = new Dictionary<int, (long, long)>();
        foreach (var kv in _counters)
            result[kv.Key] = (Interlocked.Read(ref kv.Value.Sent), Interlocked.Read(ref kv.Value.Received));
        return result;
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        IsRunning = false;
    }
}
