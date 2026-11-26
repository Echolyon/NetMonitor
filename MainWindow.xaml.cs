using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NetMonitor;

public record ConnectionRow(
    string Protocol,
    string Process,
    int Pid,
    string Local,
    string Remote,
    string Country,
    string Host,
    string State);

public record TrafficRow(
    string Process,
    int Pid,
    string Download,
    string Upload,
    string TotalDownloaded,
    string TotalUploaded);

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TrafficMonitor _traffic = new();
    private readonly RemoteInfoService _remote = new();

    private Dictionary<int, (long Sent, long Received)> _prev = new();
    private DateTime _prevTime = DateTime.UtcNow;

    public MainWindow()
    {
        InitializeComponent();

        _traffic.Start();
        TrafficNote.Text = _traffic.Error ?? "";

        Closed += (_, _) =>
        {
            _traffic.Dispose();
            _remote.Dispose();
        };

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        RefreshConnections();
        RefreshTraffic();
    }

    private void RefreshConnections()
    {
        var rows = ConnectionReader.ReadAll().Select(c =>
        {
            var ip = RemoteInfoService.ExtractIp(c.Remote);
            string country = ip == null ? "" : _remote.GetCountry(ip);
            string host = ip == null ? "" : _remote.GetHost(ip);
            return new ConnectionRow(c.Protocol, c.Process, c.Pid, c.Local, c.Remote, country, host, c.State);
        }).AsEnumerable();

        if (OnlyEstablished.IsChecked == true)
            rows = rows.Where(r => r.State == "ESTABLISHED");

        string filter = FilterBox.Text.Trim();
        if (filter.Length > 0)
            rows = rows.Where(r =>
                r.Process.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Protocol.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Remote.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Local.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Country.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Host.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var list = rows.OrderBy(r => r.Process).ThenBy(r => r.Protocol).ToList();
        Grid.ItemsSource = list;

        string geoNote = _remote.GeoAvailable ? "" : "  |  GeoIP veritabanı yok";
        StatusText.Text = $"{list.Count} bağlantı  |  {DateTime.Now:HH:mm:ss}{geoNote}";
    }

    private void RefreshTraffic()
    {
        if (!_traffic.IsRunning) return;

        var now = _traffic.Snapshot();
        var nowTime = DateTime.UtcNow;
        double seconds = Math.Max((nowTime - _prevTime).TotalSeconds, 0.1);

        string filter = FilterBox.Text.Trim();

        var rows = now
            .Where(kv => kv.Value.Sent + kv.Value.Received > 0)
            .Select(kv =>
            {
                var prev = _prev.TryGetValue(kv.Key, out var p) ? p : (Sent: 0L, Received: 0L);
                double down = (kv.Value.Received - prev.Received) / seconds;
                double up = (kv.Value.Sent - prev.Sent) / seconds;
                return (Pid: kv.Key, Down: down, Up: up, Total: kv.Value);
            })
            .Select(x => (x.Down, x.Up, Row: new TrafficRow(
                ConnectionReader.GetProcessName(x.Pid),
                x.Pid,
                FormatSpeed(x.Down),
                FormatSpeed(x.Up),
                FormatBytes(x.Total.Received),
                FormatBytes(x.Total.Sent))))
            .Where(x => filter.Length == 0 ||
                        x.Row.Process.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Down + x.Up)
            .Select(x => x.Row)
            .ToList();

        TrafficGrid.ItemsSource = rows;

        _prev = now;
        _prevTime = nowTime;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {units[i]}" : $"{v:0.##} {units[i]}";
    }

    private static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond < 1 ? "-" : FormatBytes((long)bytesPerSecond) + "/s";

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();
    private void Option_Changed(object sender, RoutedEventArgs e) => Refresh();
}