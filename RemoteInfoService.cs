using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using MaxMind.Db;

namespace NetMonitor;

/// <summary>
/// Uzak IP adresleri için ülke (çevrimdışı GeoIP veritabanı) ve
/// ters DNS adı (arka planda, önbellekli) bilgisi sağlar.
/// </summary>
public sealed class RemoteInfoService : IDisposable
{
    private readonly Reader? _geo;
    private readonly ConcurrentDictionary<string, string> _hosts = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private readonly SemaphoreSlim _dnsLimiter = new(8);

    public bool GeoAvailable => _geo != null;

    public RemoteInfoService()
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "GeoIP");
            if (Directory.Exists(dir))
            {
                string? file = Directory.GetFiles(dir, "*.mmdb").FirstOrDefault();
                if (file != null) _geo = new Reader(file);
            }
        }
        catch
        {
            _geo = null;
        }
    }

    /// <summary>"1.2.3.4:443" veya "[2001:db8::1]:443" biçimindeki metinden IP adresini çıkarır.</summary>
    public static IPAddress? ExtractIp(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint) || endpoint == "-" || endpoint == "*") return null;

        string host;
        if (endpoint.StartsWith('['))
        {
            int end = endpoint.IndexOf(']');
            if (end < 0) return null;
            host = endpoint.Substring(1, end - 1);
        }
        else
        {
            int colon = endpoint.LastIndexOf(':');
            host = colon > 0 ? endpoint.Substring(0, colon) : endpoint;
        }

        return IPAddress.TryParse(host, out var ip) ? ip : null;
    }

    public static bool IsLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            byte first = ip.GetAddressBytes()[0];
            return first == 0xFC || first == 0xFD; // fc00::/7 (özel ağ)
        }

        return false;
    }

    /// <summary>İki harfli ülke kodu (örn. "US"). Yerel adreslerde "yerel", bilinmiyorsa "-".</summary>
    public string GetCountry(IPAddress ip)
    {
        if (IsLocal(ip)) return "yerel";
        if (_geo == null) return "-";

        try
        {
            var record = _geo.Find<Dictionary<string, object>>(ip);
            if (record != null
                && record.TryGetValue("country", out var c)
                && c is Dictionary<string, object> country
                && country.TryGetValue("iso_code", out var iso))
            {
                return iso?.ToString() ?? "-";
            }
        }
        catch { }

        return "-";
    }

    /// <summary>
    /// Ters DNS adı. İlk istekte "..." döner ve arka planda çözülür;
    /// sonraki yenilemelerde sonuç hazır olur.
    /// </summary>
    public string GetHost(IPAddress ip)
    {
        if (IsLocal(ip)) return "";

        string key = ip.ToString();
        if (_hosts.TryGetValue(key, out var host)) return host;

        if (_pending.TryAdd(key, 0))
            _ = ResolveAsync(ip, key);

        return "...";
    }

    private async Task ResolveAsync(IPAddress ip, string key)
    {
        string result = "-";
        await _dnsLimiter.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var entry = await Dns.GetHostEntryAsync(key, cts.Token);
            if (!string.IsNullOrEmpty(entry.HostName) && entry.HostName != key)
                result = entry.HostName;
        }
        catch
        {
            // çözülemedi: "-" olarak kalır
        }
        finally
        {
            _dnsLimiter.Release();
            _hosts[key] = result;
            _pending.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _geo?.Dispose();
        _dnsLimiter.Dispose();
    }
}
