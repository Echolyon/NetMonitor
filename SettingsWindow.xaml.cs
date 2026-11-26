using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace NetMonitor;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;

    /// <summary>"Kaydet"e basıldıysa yeni ayarlar.</summary>
    public AppSettings Result { get; private set; }

    public SettingsWindow(AppSettings current, bool geoAvailable, string historyDbPath)
    {
        InitializeComponent();

        _original = current;
        Result = current;

        RefreshBox.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.RefreshOptions, current.RefreshSeconds));
        RetentionBox.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.RetentionOptions, current.RetentionDays));
        LevelBox.SelectedIndex = current.NotifyMinLevel == "Yüksek" ? 1 : 0;
        NotifyCheck.IsChecked = current.NotificationsOn;

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        AboutText.Text = $"NetMonitor {version} - geçmiş ve ayarlar yalnızca bu bilgisayarda tutulur. Host adları için sistemin DNS sunucusuna ters DNS sorgusu gönderilir.";
        GeoText.Text = geoAvailable
            ? "GeoIP veritabanı: yüklü"
            : "GeoIP veritabanı: bulunamadı (GeoIP klasörüne bir .mmdb dosyası ekleyin)";
        DataText.Text = $"Geçmiş: {historyDbPath}\nAyarlar: {AppSettings.FilePath}\nHata günlüğü: {AppLog.FilePath}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = new AppSettings
        {
            RefreshSeconds = AppSettings.RefreshOptions[RefreshBox.SelectedIndex],
            RetentionDays = AppSettings.RetentionOptions[RetentionBox.SelectedIndex],
            NotificationsOn = NotifyCheck.IsChecked == true,
            NotifyMinLevel = LevelBox.SelectedIndex == 1 ? "Yüksek" : "Orta"
        };
        DialogResult = true;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write("Bağlantı açılamadı", ex);
        }
        e.Handled = true;
    }
}
