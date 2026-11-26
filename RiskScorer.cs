using System.IO;

namespace NetMonitor;

public record RiskResult(int Score, string Level, string Reason);

/// <summary>
/// Bir bağlantı/süreç için basit, kural tabanlı risk puanı üretir.
/// Her kural açıklanabilir olmalı: kullanıcı "neden riskli?" sorusunun cevabını görür.
/// Bu sınıf ileride Startup Analyzer ile paylaşılacak.
/// </summary>
public static class RiskScorer
{
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "lsass", "csrss", "winlogon", "services", "smss", "wininit"
    };

    private static readonly HashSet<int> CommonPorts = new()
    {
        20, 21, 22, 25, 53, 80, 110, 123, 143, 443, 465, 587, 853, 993, 995,
        5222, 5228, 8080, 8443
    };

    private static readonly string[] SuspiciousDirs =
    {
        "\\Temp\\", "\\Downloads\\", "\\Users\\Public\\", "\\$Recycle.Bin\\"
    };

    public static RiskResult Evaluate(
        string processName, string? path, SignatureState signature,
        string protocol, string state, string remote)
    {
        int score = 0;
        var reasons = new List<string>();

        if (path != null)
        {
            bool inWindowsDir = ProcessInfoService.IsUnderWindowsDir(path);

            // 1) Sistem süreci adı, Windows klasörü dışından çalışıyor (taklit belirtisi)
            if (SystemNames.Contains(processName) && !inWindowsDir)
            {
                score += 50;
                reasons.Add("Sistem süreci adı Windows klasörü dışından çalışıyor");
            }

            // 2) Riskli konum
            bool suspiciousDir = SuspiciousDirs.Any(d => path.Contains(d, StringComparison.OrdinalIgnoreCase));
            if (suspiciousDir)
            {
                score += 40;
                reasons.Add("Temp/İndirilenler gibi bir konumdan çalışıyor");
            }
            else if (path.Contains("\\AppData\\", StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
                reasons.Add("AppData içinden çalışıyor");
            }

            // 3) İmza durumu
            switch (signature)
            {
                case SignatureState.Unsigned:
                    score += 30;
                    reasons.Add("Dijital imzası yok");
                    break;
                case SignatureState.Invalid:
                    score += 60;
                    reasons.Add("Dijital imza bozuk (dosya değiştirilmiş olabilir)");
                    break;
            }
        }

        // 4) Alışılmadık uzak port (sadece kurulmuş TCP bağlantıları)
        if (state == "ESTABLISHED")
        {
            var ip = RemoteInfoService.ExtractIp(remote);
            if (ip != null && !RemoteInfoService.IsLocal(ip))
            {
                int colon = remote.LastIndexOf(':');
                if (colon > 0 && int.TryParse(remote.AsSpan(colon + 1), out int port)
                    && !CommonPorts.Contains(port))
                {
                    score += 10;
                    reasons.Add($"Alışılmadık port ({port})");
                }
            }
        }

        string level = score >= 50 ? "Yüksek" : score >= 20 ? "Orta" : "Düşük";
        return new RiskResult(score, level, string.Join("; ", reasons));
    }
}
