using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    string State,
    string Risk,
    string Signature,
    string Reason,
    string Path);

public record TrafficRow(
    string Process,
    int Pid,
    string Download,
    string Upload,
    string TotalDownloaded,
    string TotalUploaded);

public partial class MainWindow : Window
{
    private const string SignaturePending = "Kontrol ediliyor";
    private const int NotificationWarmupSeconds = 20;

    private readonly DispatcherTimer _timer = new();
    private readonly TrafficMonitor _traffic = new();
    private readonly RemoteInfoService _remote = new();
    private readonly ProcessInfoService _procInfo = new();
    private readonly HistoryStore _history;
    private AppSettings _settings;

    private Dictionary<int, (long Sent, long Received)> _prev = new();
    private DateTime _prevTime = DateTime.UtcNow;
    private List<ConnectionRow> _lastRows = new();
    private bool _ready;

    // sistem tepsisi
    private readonly System.Windows.Forms.NotifyIcon _tray = new();
    private bool _inTray;
    private bool _trayHintShown;
    private bool _notificationsOn = true;
    private System.Windows.Forms.ToolStripMenuItem? _notifyItem;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public MainWindow()
    {
        _settings = AppSettings.Load();
        _history = new HistoryStore(_settings.RetentionDays);
        _notificationsOn = _settings.NotificationsOn;
        _timer.Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds);

        InitializeComponent();

        _traffic.Start();
        TrafficNote.Text = _traffic.Error ?? "";

        SetupTray();

        Closed += (_, _) =>
        {
            _timer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            _traffic.Dispose();
            _remote.Dispose();
        };

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        _ready = true;
        Refresh();
    }

    private void Refresh()
    {
        var allRows = CollectRows();
        RecordHistory(allRows);
        UpdateTrayText(allRows);

        // Pencere tepsideyken arayüz güncellenmez; kayıt ve bildirimler çalışmaya devam eder.
        if (_inTray) return;

        ShowConnections(allRows);
        RefreshTraffic();
        RefreshHistory();
    }

    // ------------------------------------------------------------ sistem tepsisi

    private void SetupTray()
    {
        try
        {
            _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                         ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            _tray.Icon = System.Drawing.SystemIcons.Application;
        }

        _tray.Text = "NetMonitor";

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Aç", null, (_, _) => ShowFromTray());

        menu.Items.Add("Ayarlar...", null, (_, _) =>
        {
            ShowFromTray();
            OpenSettings();
        });

        var notifyItem = new System.Windows.Forms.ToolStripMenuItem("Bildirimler")
        {
            Checked = _notificationsOn,
            CheckOnClick = true
        };
        notifyItem.CheckedChanged += (_, _) =>
        {
            _notificationsOn = notifyItem.Checked;
            _settings.NotificationsOn = _notificationsOn;
            _settings.Save();
        };
        _notifyItem = notifyItem;
        menu.Items.Add(notifyItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => Close());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
        _tray.Visible = true;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        // Küçültünce görev çubuğundan kalkıp tepsiye iner. (X düğmesi uygulamayı kapatır.)
        if (WindowState == WindowState.Minimized)
        {
            _inTray = true;
            Hide();

            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(4000, "NetMonitor",
                    "Arka planda çalışmaya devam ediyor. Açmak için simgeye çift tıkla, kapatmak için sağ tıkla > Çıkış.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        }
    }

    public void ShowFromTray()
    {
        _inTray = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Refresh();
    }

    private void UpdateTrayText(List<ConnectionRow> rows)
    {
        int risky = rows.Count(r => r.Risk != "Düşük");
        // NotifyIcon.Text en fazla 63 karakter olabilir.
        _tray.Text = risky > 0 ? $"NetMonitor - {risky} riskli bağlantı" : "NetMonitor";
    }

    private void NotifyNewRisky(List<ConnectionRow> created)
    {
        if (!_notificationsOn) return;
        if ((DateTime.UtcNow - _startedAt).TotalSeconds < NotificationWarmupSeconds) return;

        bool onlyHigh = _settings.NotifyMinLevel == "Yüksek";
        var risky = created
            .Where(r => r.Risk == "Yüksek" || (!onlyHigh && r.Risk == "Orta"))
            .ToList();
        if (risky.Count == 0) return;

        string title;
        string text;
        if (risky.Count == 1)
        {
            var r = risky[0];
            title = $"Yeni riskli bağlantı ({r.Risk})";
            text = $"{r.Process} → {r.Remote} ({r.Country})";
            if (r.Reason.Length > 0) text += "\n" + r.Reason;
        }
        else
        {
            title = $"{risky.Count} yeni riskli bağlantı";
            text = string.Join("\n", risky.Take(3).Select(r => $"{r.Process} → {r.Remote}"));
            if (risky.Count > 3) text += $"\n... ve {risky.Count - 3} tane daha";
        }

        if (text.Length > 250) text = text.Substring(0, 247) + "...";
        _tray.ShowBalloonTip(6000, title, text, System.Windows.Forms.ToolTipIcon.Warning);
    }

    // ------------------------------------------------------------ bağlantılar

    private List<ConnectionRow> CollectRows()
    {
        return ConnectionReader.ReadAll().Select(c =>
        {
            var ip = RemoteInfoService.ExtractIp(c.Remote);
            string country = ip == null ? "" : _remote.GetCountry(ip);
            string host = ip == null ? "" : _remote.GetHost(ip);

            string? path = _procInfo.GetPath(c.Pid);
            var signature = path == null ? SignatureState.Unknown : _procInfo.GetSignature(path);
            var risk = RiskScorer.Evaluate(c.Process, path, signature, c.Protocol, c.State, c.Remote);

            return new ConnectionRow(
                c.Protocol, c.Process, c.Pid, c.Local, c.Remote, country, host, c.State,
                risk.Level, path == null ? "-" : SignatureText(signature), risk.Reason, path ?? "");
        }).ToList();
    }

    private void RecordHistory(List<ConnectionRow> allRows)
    {
        // Geçmişe, ekrandaki filtrelerden bağımsız olarak tüm dış bağlantılar yazılır.
        // İmzası henüz kontrol edilen satırlar bir sonraki yenilemeye bırakılır
        // (puan eksik hesaplanıp kaydedilmesin diye).
        var toRecord = allRows
            .Where(r => r.State == "ESTABLISHED"
                        && r.Country != "" && r.Country != "yerel"
                        && r.Signature != SignaturePending)
            .ToList();
        if (toRecord.Count == 0) return;

        _ = Task.Run(() =>
        {
            var created = _history.Record(toRecord);
            if (created.Count > 0)
                Dispatcher.InvokeAsync(() => NotifyNewRisky(created));
        });
    }

    private void ShowConnections(List<ConnectionRow> allRows)
    {
        var rows = allRows.AsEnumerable();

        string protocol = (ProtocolBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Tümü";
        if (protocol != "Tümü")
            rows = rows.Where(r => r.Protocol == protocol);

        if (OnlyEstablished.IsChecked == true)
            rows = rows.Where(r => r.State == "ESTABLISHED");

        if (OnlyRisky.IsChecked == true)
            rows = rows.Where(r => r.Risk != "Düşük");

        string filter = FilterBox.Text.Trim();
        if (filter.Length > 0)
            rows = rows.Where(r =>
                r.Process.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Protocol.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Remote.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Local.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Country.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Host.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Path.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var list = rows
            .OrderByDescending(r => r.Risk == "Yüksek" ? 2 : r.Risk == "Orta" ? 1 : 0)
            .ThenBy(r => r.Process)
            .ThenBy(r => r.Protocol)
            .ToList();

        // Yenilemede kaydırma konumu ve seçili satır korunur.
        var scroll = FindScrollViewer(Grid);
        double offset = scroll?.VerticalOffset ?? 0;
        string? selectedKey = (Grid.SelectedItem as ConnectionRow) is { } sel ? RowKey(sel) : null;

        Grid.ItemsSource = list;
        Grid.UpdateLayout();

        if (selectedKey != null)
        {
            var again = list.FirstOrDefault(r => RowKey(r) == selectedKey);
            if (again != null) Grid.SelectedItem = again;
        }
        scroll?.ScrollToVerticalOffset(offset);

        _lastRows = list;

        int risky = list.Count(r => r.Risk != "Düşük");
        string geoNote = _remote.GeoAvailable ? "" : "  |  GeoIP veritabanı yok";
        StatusText.Text = $"{list.Count} bağlantı  |  {risky} riskli  |  {DateTime.Now:HH:mm:ss}{geoNote}";
    }

    private static string RowKey(ConnectionRow r) => $"{r.Protocol}|{r.Pid}|{r.Local}|{r.Remote}";

    private static string SignatureText(SignatureState s) => s switch
    {
        SignatureState.Signed => "İmzalı",
        SignatureState.Unsigned => "İmzasız",
        SignatureState.Invalid => "Bozuk",
        SignatureState.WindowsFile => "Windows",
        SignatureState.Pending => SignaturePending,
        _ => "Doğrulanamadı"
    };

    // ------------------------------------------------------------------ trafik

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

        var scroll = FindScrollViewer(TrafficGrid);
        double offset = scroll?.VerticalOffset ?? 0;

        TrafficGrid.ItemsSource = rows;
        TrafficGrid.UpdateLayout();
        scroll?.ScrollToVerticalOffset(offset);

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

    // ------------------------------------------------------------------ geçmiş

    private void RefreshHistory()
    {
        if (!HistoryTab.IsSelected) return;

        if (_history.Error != null)
        {
            HistoryStatus.Text = _history.Error;
            return;
        }

        long seconds = PeriodBox.SelectedIndex switch
        {
            0 => 3600,
            1 => 86400,
            2 => 7 * 86400,
            _ => 30 * 86400
        };
        long since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds;

        var entries = _history.Query(since, OnlyRisky.IsChecked == true).AsEnumerable();

        string filter = FilterBox.Text.Trim();
        if (filter.Length > 0)
            entries = entries.Where(e =>
                e.Process.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Destination.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Country.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Host.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var list = entries.ToList();

        var scroll = FindScrollViewer(HistoryGrid);
        double offset = scroll?.VerticalOffset ?? 0;

        HistoryGrid.ItemsSource = list;
        HistoryGrid.UpdateLayout();
        scroll?.ScrollToVerticalOffset(offset);

        HistoryStatus.Text = $"{list.Count} hedef  |  {_history.RetentionDays} gün saklanır  |  {_history.DbPath}";
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Tüm bağlantı geçmişi silinecek. Devam edilsin mi?",
            "NetMonitor", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _history.Clear();
        RefreshHistory();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // İçteki ComboBox'ların SelectionChanged olayı da buraya yükselir; onları atla.
        if (!ReferenceEquals(e.Source, Tabs)) return;
        if (_ready) RefreshHistory();
    }

    // ------------------------------------------------------- sağ tık menüsü

    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row != null)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    private void CopyRemoteIp_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not ConnectionRow r) return;
        var ip = RemoteInfoService.ExtractIp(r.Remote);
        Clipboard.SetText(ip?.ToString() ?? r.Remote);
    }

    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not ConnectionRow r) return;
        Clipboard.SetText(string.Join("\t",
            r.Protocol, r.Process, r.Pid, r.Local, r.Remote, r.Country, r.Host,
            r.State, r.Risk, r.Signature, r.Reason, r.Path));
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not ConnectionRow r || r.Path.Length == 0 || !File.Exists(r.Path))
            return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{r.Path}\"",
            UseShellExecute = true
        });
    }

    // ------------------------------------------------------------- CSV dışa aktarma

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRows.Count == 0) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV dosyası (*.csv)|*.csv",
            FileName = $"netmonitor-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Protocol;Process;Pid;Local;Remote;Country;Host;State;Risk;Signature;Reason;Path");
        foreach (var r in _lastRows)
        {
            sb.AppendLine(string.Join(";", new[]
            {
                r.Protocol, r.Process, r.Pid.ToString(), r.Local, r.Remote, r.Country,
                r.Host, r.State, r.Risk, r.Signature, r.Reason, r.Path
            }.Select(Csv)));
        }

        try
        {
            // UTF-8 + BOM: Excel Türkçe karakterleri doğru gösterir.
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Dosya kaydedilemedi: " + ex.Message, "NetMonitor",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    // ---------------------------------------------------------------- ayarlar

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_settings, _remote.GeoAvailable, _history.DbPath) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _settings = dialog.Result;
        _settings.Save();
        ApplySettings();
    }

    private void ApplySettings()
    {
        _timer.Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
        _notificationsOn = _settings.NotificationsOn;
        if (_notifyItem != null) _notifyItem.Checked = _notificationsOn;
        _history.SetRetention(_settings.RetentionDays);
        Refresh();
    }

    // ------------------------------------------------------------ yardımcılar

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T match) return match;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        int count = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(d, i));
            if (found != null) return found;
        }
        return null;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready) Refresh();
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) Refresh();
    }
}
