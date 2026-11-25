using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NetMonitor;

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

    private Dictionary<int, (long Sent, long Received)> _prev = new();
    private DateTime _prevTime = DateTime.UtcNow;

    public MainWindow()
    {
        InitializeComponent();

        _traffic.Start();
        TrafficNote.Text = _traffic.Error ?? "";

        Closed += (_, _) => _traffic.Dispose();
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
        var connections = ConnectionReader.ReadAll().AsEnumerable();

        if (OnlyEstablished.IsChecked == true)
            connections = connections.Where(c => c.State == "ESTABLISHED");

        string filter = FilterBox.Text.Trim();
        if (filter.Length > 0)
            connections = connections.Where(c =>
                c.Process.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.Protocol.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.Remote.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.Local.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var list = connections.OrderBy(c => c.Process).ThenBy(c => c.Protocol).ToList();
        Grid.ItemsSource = list;
        StatusText.Text = $"{list.Count} bağlantı  |  {DateTime.Now:HH:mm:ss}";
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