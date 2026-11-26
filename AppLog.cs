using System.IO;

namespace NetMonitor;

/// <summary>Hataları %LocalAppData%\NetMonitor\error.log dosyasına yazar.</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetMonitor", "error.log");

    public static void Write(string context, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                // Dosya şişmesin: 1 MB'ı geçerse sıfırla.
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                    File.Delete(FilePath);

                File.AppendAllText(FilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex}{Environment.NewLine}");
            }
        }
        catch
        {
            // Günlük yazılamıyorsa sessizce geç.
        }
    }
}
