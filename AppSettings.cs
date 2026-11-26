using System.IO;
using System.Text.Json;

namespace NetMonitor;

/// <summary>Kullanıcı ayarları. %LocalAppData%\NetMonitor\settings.json dosyasında saklanır.</summary>
public sealed class AppSettings
{
    public static readonly int[] RefreshOptions = { 1, 2, 5, 10 };
    public static readonly int[] RetentionOptions = { 7, 30, 90, 365 };

    /// <summary>Tabloların yenilenme aralığı (saniye).</summary>
    public int RefreshSeconds { get; set; } = 2;

    /// <summary>Bağlantı geçmişinin saklanma süresi (gün).</summary>
    public int RetentionDays { get; set; } = 30;

    public bool NotificationsOn { get; set; } = true;

    /// <summary>"Orta" = Orta ve Yüksek için bildir, "Yüksek" = yalnızca Yüksek için.</summary>
    public string NotifyMinLevel { get; set; } = "Orta";

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetMonitor", "settings.json");

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Write("Ayarlar okunamadı", ex);
        }

        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Write("Ayarlar kaydedilemedi", ex);
        }
    }

    /// <summary>Elle bozulmuş/geçersiz değerleri varsayılana çeker.</summary>
    private void Normalize()
    {
        if (!RefreshOptions.Contains(RefreshSeconds)) RefreshSeconds = 2;
        if (!RetentionOptions.Contains(RetentionDays)) RetentionDays = 30;
        if (NotifyMinLevel != "Orta" && NotifyMinLevel != "Yüksek") NotifyMinLevel = "Orta";
    }
}
