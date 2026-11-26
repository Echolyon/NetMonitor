# NetMonitor

Windows için hafif, tamamen yerel çalışan **süreç bazlı ağ trafiği izleyici**. Hangi uygulamanın hangi sunucuyla konuştuğunu, her sürecin şu an ne kadar bant genişliği kullandığını gösterir ve şüpheli görünen bağlantıları işaretler. Tüm veriler bilgisayarında kalır.

English: [README.md](README.md)

![Bağlantılar](docs/connections.png)
![Geçmiş](docs/history.png)

## Özellikler

- IPv4 ve IPv6 üzerinde TCP ve UDP için **canlı bağlantı tablosu**: süreç, PID, yerel/uzak adres, durum
- Event Tracing for Windows (ETW) ile **süreç başına bant genişliği** (indirme/yükleme hızı ve toplamlar)
- Uzak adreslerin **ülkesi ve host adı** (çevrimdışı GeoIP veritabanı + ters DNS)
- Her işaret için açık bir gerekçe veren **risk ipuçları**
- Yerel SQLite veritabanında dönem bazlı aranabilir **bağlantı geçmişi**
- Yeni bir riskli hedef göründüğünde **sistem tepsisi simgesi ve bildirim**
- Sağ tık menüsü (IP kopyala, dosya konumunu aç), CSV dışa aktarma, protokol/risk filtreleri

## Gereksinimler

- Windows 10 / 11, 64 bit
- **Yönetici yetkisi** (ETW ağ olaylarını dinlemek için gerekir; Windows UAC onayı ister)
- Başka bir şey gerekmez: yayın sürümü kendi çalışma ortamını içerir, .NET kurmaya gerek yoktur

## İndirme ve çalıştırma

1. [Releases](../../releases) sayfasından en son zip dosyasını indirip bir klasöre çıkar.
2. `NetMonitor.exe` dosyasını çalıştır ve UAC isteğini onayla.
3. *(İsteğe bağlı)* Ülke bilgisi için GeoIP veritabanını ekle (aşağıya bak).

Çalıştırılabilir dosya kod imzalı olmadığından Windows SmartScreen "tanınmayan uygulama" uyarısı verebilir. İndirdiğin dosyayı her sürümle birlikte yayımlanan SHA-256 özetiyle doğrulayabilir veya kaynak koddan kendin derleyebilirsin.

### Ülke bilgisi (isteğe bağlı)

1. <https://db-ip.com/db/download/ip-to-country-lite> adresinden **IP to Country Lite** veritabanını MMDB biçiminde indir.
2. `.mmdb` dosyasını çıkarıp `NetMonitor.exe`'nin yanındaki `GeoIP` klasörüne koy.
3. NetMonitor'ü yeniden başlat.

## Risk ipuçları nasıl çalışır?

Kurallar basit, toplamalı ve açıklanabilirdir. *Reason* sütunu hangi kuralların çalıştığını gösterir.

| Kural | Puan |
|---|---|
| Sistem süreci adı (`svchost`, `lsass` vb.) Windows klasörü dışından çalışıyor | +50 |
| Temp, İndirilenler, Users\Public veya Geri Dönüşüm Kutusu'ndan çalışıyor | +40 |
| Geçerli dijital imzası yok | +30 |
| İmza var ama geçersiz (dosya değiştirilmiş) | +60 |
| AppData içinden çalışıyor | +10 |
| Dış adrese alışılmadık portla kurulmuş bağlantı | +10 |

20 ve üzeri = *Orta* (sarı), 50 ve üzeri = *Yüksek* (kırmızı).

**Sınırlar, lütfen oku:** Bu bir ipucudur, hüküm değildir. Birçok meşru program imzasızdır veya AppData'da çalışır, kötü amaçlı yazılımlar da çalıntı sertifikalarla imzalanabilir. NetMonitor bir antivirüs değildir ve hiçbir şeyi engellemez. Katalog imzalı Windows sistem dosyaları, Windows klasöründeyse normal kabul edilir.

## Gizlilik

- Bağlantı geçmişi, ayarlar ve hata günlüğü yalnızca bilgisayarında, `%LocalAppData%\NetMonitor\` altında tutulur.
- Telemetri yok, hesap yok, herhangi bir NetMonitor sunucusuna bağlantı yok.
- Ülke sorgusu tamamen çevrimdışıdır.
- Host adları **ters DNS** ile çözülür; yani uzak IP adresleri, her DNS sorgusu gibi, *yapılandırdığın DNS sunucusuna* gider.
- Geçmişi *Geçmiş* sekmesinden istediğin zaman silebilir, ne kadar süre saklanacağını *Ayarlar*'dan seçebilirsin.

## Kaynak koddan derleme

Gereksinim: Visual Studio 2022 (".NET masaüstü geliştirme" iş yükü) veya .NET 8 SDK.

```powershell
dotnet build -c Release
```

Tek dosyalık yayın paketi için:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Teknoloji: C# / .NET 8, WPF, IP Helper API, ETW (`Microsoft.Diagnostics.Tracing.TraceEvent`), SQLite (`Microsoft.Data.Sqlite`), MMDB okumak için `MaxMind.Db`.

## Bilinen sınırlar

- Yalnızca Windows; arayüz şimdilik yalnızca Türkçe
- Windows ile birlikte otomatik başlamaz (uygulama yükseltilmiş yetki istediği için zamanlanmış görev gerekir; planlandı)
- Gösterir ve uyarır ama bağlantıları engelleyemez
- ETW'den gelen süreç bazlı sayaçlar az miktarda yerel (loopback) trafiği de içerebilir

## Üçüncü taraf bileşenler ve veriler

- [DB-IP IP to Country Lite](https://db-ip.com), CC BY 4.0 lisanslı (**IP Geolocation by DB-IP**). Veritabanı pakete dahil değildir, kullanıcı kendisi indirir.
- Microsoft.Diagnostics.Tracing.TraceEvent (MIT), Microsoft.Data.Sqlite (MIT), MaxMind.Db (Apache-2.0)

## Lisans

[MIT](LICENSE.txt)
