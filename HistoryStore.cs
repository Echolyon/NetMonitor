using System.IO;
using Microsoft.Data.Sqlite;

namespace NetMonitor;

public record HistoryEntry(
    string FirstSeen,
    string LastSeen,
    string Process,
    string Protocol,
    string Destination,
    string Country,
    string Host,
    string Risk,
    string Signature,
    string Reason,
    string Path);

/// <summary>
/// Dış IP'lere kurulan bağlantıları yerel bir SQLite veritabanına kaydeder.
/// Her satır bir "hedef"tir (süreç + protokol + uzak IP + uzak port); aynı hedef
/// tekrar görüldüğünde yeni satır açılmaz, "son görülme" zamanı güncellenir.
/// Veri yalnızca bu bilgisayarda, %LocalAppData%\NetMonitor\history.db içinde durur.
/// </summary>
public sealed class HistoryStore
{
    /// <summary>Kayıtların saklanma süresi (gün). Ayarlardan gelir.</summary>
    public int RetentionDays { get; private set; }

    private readonly string _connectionString;
    private readonly object _writeLock = new();

    public string DbPath { get; }

    /// <summary>Veritabanı hiç açılamadıysa hata metni.</summary>
    public string? Error { get; private set; }

    public HistoryStore(int retentionDays)
    {
        RetentionDays = retentionDays;
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetMonitor");
        DbPath = Path.Combine(dir, "history.db");

        try
        {
            Directory.CreateDirectory(dir);
            _connectionString = new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();
            Init();
        }
        catch (Exception ex)
        {
            _connectionString = "";
            Error = "Geçmiş veritabanı açılamadı: " + ex.Message;
        }
    }

    private void Init()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS destinations (
                    key         TEXT PRIMARY KEY,
                    first_seen  INTEGER NOT NULL,
                    last_seen   INTEGER NOT NULL,
                    process     TEXT NOT NULL,
                    path        TEXT NOT NULL,
                    protocol    TEXT NOT NULL,
                    remote_ip   TEXT NOT NULL,
                    remote_port INTEGER NOT NULL,
                    country     TEXT NOT NULL,
                    host        TEXT NOT NULL,
                    risk        TEXT NOT NULL,
                    signature   TEXT NOT NULL,
                    reason      TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_destinations_last_seen ON destinations(last_seen);";
            cmd.ExecuteNonQuery();
        }

        // Saklama süresi dolan kayıtları sil
        using (var prune = conn.CreateCommand())
        {
            prune.CommandText = "DELETE FROM destinations WHERE last_seen < $cutoff";
            prune.Parameters.AddWithValue("$cutoff",
                DateTimeOffset.UtcNow.AddDays(-RetentionDays).ToUnixTimeSeconds());
            prune.ExecuteNonQuery();
        }
    }

    /// <summary>Satırları kaydeder. Daha önce hiç görülmemiş hedeflerin satırlarını döndürür.</summary>
    public List<ConnectionRow> Record(IReadOnlyList<ConnectionRow> rows)
    {
        if (Error != null || rows.Count == 0) return new List<ConnectionRow>();

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var created = new List<ConnectionRow>();

        lock (_writeLock)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var tx = conn.BeginTransaction();

                foreach (var row in rows)
                {
                    var ip = RemoteInfoService.ExtractIp(row.Remote);
                    if (ip == null) continue;

                    int port = ExtractPort(row.Remote);
                    string key = $"{(row.Path.Length > 0 ? row.Path : row.Process).ToLowerInvariant()}|{row.Protocol}|{ip}|{port}";

                    using var update = conn.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText = @"
                        UPDATE destinations SET
                            last_seen = $now,
                            country   = $country,
                            host      = CASE WHEN $host IN ('', '...') THEN host ELSE $host END,
                            risk      = $risk,
                            signature = $signature,
                            reason    = $reason
                        WHERE key = $key";
                    update.Parameters.AddWithValue("$now", now);
                    update.Parameters.AddWithValue("$country", row.Country);
                    update.Parameters.AddWithValue("$host", row.Host);
                    update.Parameters.AddWithValue("$risk", row.Risk);
                    update.Parameters.AddWithValue("$signature", row.Signature);
                    update.Parameters.AddWithValue("$reason", row.Reason);
                    update.Parameters.AddWithValue("$key", key);

                    if (update.ExecuteNonQuery() > 0) continue;

                    using var insert = conn.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = @"
                        INSERT INTO destinations
                            (key, first_seen, last_seen, process, path, protocol,
                             remote_ip, remote_port, country, host, risk, signature, reason)
                        VALUES
                            ($key, $now, $now, $process, $path, $protocol,
                             $ip, $port, $country, $host, $risk, $signature, $reason)";
                    insert.Parameters.AddWithValue("$key", key);
                    insert.Parameters.AddWithValue("$now", now);
                    insert.Parameters.AddWithValue("$process", row.Process);
                    insert.Parameters.AddWithValue("$path", row.Path);
                    insert.Parameters.AddWithValue("$protocol", row.Protocol);
                    insert.Parameters.AddWithValue("$ip", ip.ToString());
                    insert.Parameters.AddWithValue("$port", port);
                    insert.Parameters.AddWithValue("$country", row.Country);
                    insert.Parameters.AddWithValue("$host", row.Host == "..." ? "" : row.Host);
                    insert.Parameters.AddWithValue("$risk", row.Risk);
                    insert.Parameters.AddWithValue("$signature", row.Signature);
                    insert.Parameters.AddWithValue("$reason", row.Reason);
                    insert.ExecuteNonQuery();
                    created.Add(row);
                }

                tx.Commit();
            }
            catch
            {
                // Geçici kilitlenme vb.: bir sonraki yenilemede tekrar denenir.
            }
        }

        return created;
    }

    /// <summary>Son görülmesi 'sinceUnix' anından sonra olan hedefler, en yeni ilk görülen en üstte.</summary>
    public List<HistoryEntry> Query(long sinceUnix, bool onlyRisky, int limit = 2000)
    {
        var result = new List<HistoryEntry>();
        if (Error != null) return result;

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT first_seen, last_seen, process, protocol, remote_ip, remote_port,
                       country, host, risk, signature, reason, path
                FROM destinations
                WHERE last_seen >= $since AND ($risky = 0 OR risk <> 'Düşük')
                ORDER BY first_seen DESC
                LIMIT $limit";
            cmd.Parameters.AddWithValue("$since", sinceUnix);
            cmd.Parameters.AddWithValue("$risky", onlyRisky ? 1 : 0);
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string ip = reader.GetString(4);
                int port = reader.GetInt32(5);
                string destination = ip.Contains(':') ? $"[{ip}]:{port}" : $"{ip}:{port}";

                result.Add(new HistoryEntry(
                    FormatTime(reader.GetInt64(0)),
                    FormatTime(reader.GetInt64(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    destination,
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11)));
            }
        }
        catch
        {
            // okunamadıysa boş liste döner
        }

        return result;
    }

    /// <summary>Saklama süresini değiştirir ve süresi dolan kayıtları hemen siler.</summary>
    public void SetRetention(int days)
    {
        RetentionDays = days;
        if (Error != null) return;

        lock (_writeLock)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM destinations WHERE last_seen < $cutoff";
                cmd.Parameters.AddWithValue("$cutoff",
                    DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    public void Clear()
    {
        if (Error != null) return;

        lock (_writeLock)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM destinations";
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    private static string FormatTime(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static int ExtractPort(string endpoint)
    {
        int colon = endpoint.LastIndexOf(':');
        return colon > 0 && int.TryParse(endpoint.AsSpan(colon + 1), out int port) ? port : 0;
    }
}
