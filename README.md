# Wothenza Harvester

Telegram free/crack kanallarını izleyen, dosyaları indiren, dedupe eden, analiz eden ve sana bot üzerinden ileten C# servisi. Üstünde zamanlanmış post motoru ve premium web dashboard var.

## mimari

```
WTelegram userbot  ──►  tüm dialoglar (kanal/grup/DM/saved)
        │                mesaj geçmişi + doküman indirme
        ▼
   SHA-256 dedupe  ──►  SQLite (files/sources/queue)
        │
        ▼
   magic-byte + VirusTotal analiz
        │
        ├──►  Bot API (@denemepythonBOT)  →  sana DM (≤50 MB)
        └──►  userbot Saved Messages      →  büyük dosyalar
        │
        ▼
   zamanlanmış poster  ──►  @wothenza
        │
        ▼
   ASP.NET Core dashboard + REST API
```

## özellikler

- **otomatik kaynak tarama** — hesabındaki tüm kanal, grup, DM ve Saved Messages
- **dosya avcısı** — `.exe .zip .rar .7z .apk .apks .xapk .ipa .tipa .dmg .msi .jar ...`
- **sha256 dedupe** — aynı dosya ikinci kez gönderilmez
- **kanal ayrımı** — `DEST_CHANNEL` önce taranır; orada zaten olan dosyalar (doc id + dosya adı/boyut) sana tekrar gönderilmez, dashboard'da "kanalda var" olarak ayrılır
- **orijinal tarih** — her dosyanın kanala ilk atıldığı tarih (`msg_date`) kaydedilir ve dashboard'da gösterilir
- **magic byte analizi** — uzantı sahtekarlığını yakalar (`.exe` aslında zip ise `suspicious`); büyük dosyalarda 64 KB header analizi
- **VirusTotal** — opsiyonel, hash lookup + küçük dosya upload
- **bot teslimatı** — bulunan her dosya `@denemepythonBOT` üzerinden sana gelir
- **50 MB üstü** — önce forward/resend, olmazsa userbot ile Saved Messages'a yüklenir, bot not düşer
- **dry-run** — `DRY_RUN=true` ile hiçbir şey göndermeden tara/analiz et
- **zamanlanmış post** — tek seferlik / günlük / haftalık, dashboard'dan
- **arşiv** — `/data/archive/<hash>.<ext>`
- **dashboard** — koyu tema, canlı istatistik, dosya arama, "yeni / bana gelenler / kanalda var" filtreleri, kaynak listesi, log akışı
- **REST API** — her şey script'ten de kontrol edilebilir

## yerel kurulum

```powershell
cd "C:\Users\ozere\Documents\Varsayılan Proje\WothenzaHarvester"

$env:TG_API_ID       = "33605133"
$env:TG_API_HASH     = "1c1ff0d54c081452c21676be6d447f6a"
$env:TG_PHONE        = "+905067003608"
$env:BOT_TOKEN       = "9218251077:AAHaaJ5K1Z_O5t5WF_w6Eud06EdZVpj5Ixs"
$env:OWNER_CHAT_ID   = "8821782197"
$env:DEST_CHANNEL    = "wothenza"

# ilk giriş (interaktif terminal — kod sorar)
dotnet run -- login

# agent / headless ortamda giriş (kod ve 2FA'yı HTTP ile ver)
dotnet run -- login-server
# -> POST http://127.0.0.1:10098/code      (raw body: 12345)
# -> POST http://127.0.0.1:10098/password  (raw body: 2fa)

# bot token testi (sana test mesajı atar)
dotnet run -- bottest

# kanalda en büyük dosyayı bul, 64KB header indir, analiz et (göndermez)
dotnet run -- probe wothenza

# tek seferlik tarama
dotnet run -- scan

# hiçbir şey göndermeden tara
$env:DRY_RUN="true"; dotnet run -- scan

# web + zamanlayıcı
dotnet run
# -> http://localhost:10000
```

> `dotnet run -- login` komutunu kendi terminalinde çalıştır; agent shell'i interaktif değil. Headless için `login-server` kullan.

## Render deploy

1. projeyi git reposuna koy
2. Render → **New → Blueprint** → `render.yaml`
3. env var'ları doldur (`sync: false` olanlar): `TG_API_ID`, `TG_API_HASH`, `TG_PHONE`, `BOT_TOKEN`, `TG_SESSION_B64`, `DASH_PASSWORD`
4. deploy

### session'ı Render'a taşıma

Render'da interaktif login yok. yerelde login olup session'ı base64 olarak taşı:

```powershell
dotnet run -- export-session
```

çıktıyı `TG_SESSION_B64` env var'ına yapıştır. uygulama açılışta `/data/wz.session` dosyasına çözer.

> `render.yaml` içindeki disk **starter** planda çalışır. free plan ephemeral disk verir; o zaman `TG_SESSION_B64` her deploy'da yeterli olur ama DB/arşiv sıfırlanır.

## env değişkenleri

| değişken | varsayılan | açıklama |
|---|---|---|
| `TG_API_ID` | — | my.telegram.org |
| `TG_API_HASH` | — | my.telegram.org |
| `TG_PHONE` | — | login için |
| `TG_PASSWORD` | — | 2FA varsa |
| `TG_SESSION_PATH` | `data/wz.session` | session dosyası |
| `TG_SESSION_B64` | — | base64 session (Render) |
| `BOT_TOKEN` | — | `@denemepythonBOT` token |
| `OWNER_CHAT_ID` | `8821782197` | dosyaların gideceği hesap |
| `DEST_CHANNEL` | `wothenza` | zamanlanmış postlar |
| `DB_PATH` | `data/wz.db` | SQLite |
| `ARCHIVE_DIR` | `data/archive` | arşiv |
| `TEMP_DIR` | `data/tmp` | geçici indirme |
| `SCAN_INTERVAL_MIN` | `30` | tarama aralığı |
| `SCAN_DEPTH` | `200` | ilk tarama derinliği / kaynak |
| `DEST_DEPTH` | `2000` | `DEST_CHANNEL` için indeks derinliği |
| `SCAN_MAX_PER_SOURCE` | `2000` | artımlı tarama üst limiti |
| `SCAN_MAX_SOURCES` | `0` | test için kaynak sayısı limiti (0 = sınırsız) |
| `SCAN_ONLY` | — | virgülle kaynak filtresi (username veya başlık parçası) |
| `DM_DEPTH` | `100` | DM başına mesaj |
| `MAX_FILE_MB` | `400` | indirip analiz edilecek maksimum dosya boyutu |
| `FORWARD_MAX_MB` | `4000` | indirmeden forward/resend denenecek maksimum boyut |
| `ARCHIVE_MAX_MB` | `100` | diske arşivlenecek maksimum dosya boyutu |
| `SCAN_DMS` | `true` | DM kutularını tara |
| `SCAN_SAVED` | `true` | Saved Messages tara |
| `ARCHIVE` | `true` | dosyaları diske sakla |
| `DRY_RUN` | `false` | hiçbir şey gönderme, sadece tara/analiz et |
| `EXTENSIONS` | liste | virgülle özel liste |
| `KEYWORDS` | liste | virgülle özel liste |
| `VT_API_KEY` | — | VirusTotal v3 |
| `VT_UPLOAD` | `false` | VT'ye dosya yükle (≤32MB) |
| `DASH_PASSWORD` | — | dashboard/API koruması |
| `PORT` | `10000` | HTTP portu |

## API

```
GET    /api/stats
GET    /api/files?q=&limit=&scope=all|new|sent|dest
GET    /api/sources
GET    /api/queue
POST   /api/queue        { channel, text, scheduleAt, repeat, mediaPath }
POST   /api/queue/{id}/run
DELETE /api/queue/{id}
POST   /api/scan
GET    /api/logs?take=
GET    /api/session
GET    /healthz
```

`DASH_PASSWORD` set ise `/api/*` için `x-dash-key` header veya `?key=` gerekir.

## notlar

- Bot API doküman limiti **50 MB**. üstü önce forward/resend ile Saved Messages'a aktarılır; olmazsa (kanal kaydetmeyi kısıtlıyorsa) indirilip yeniden yüklenir.
- `DEST_CHANNEL` her taramada önce indekslenir (varsayılan 2000 mesaj). Buradaki dosyalar dashboard'da **kanalda var** olarak görünür ve sana gönderilmez. Başka kanallarda aynı dosya kimliği veya aynı ad+boyut görülürse o da atlanır.
- Her dosyanın orijinal mesaj tarihi `msg_date` olarak saklanır; dashboard'daki ilk sütun bunu gösterir.
- Kaynak kanal "içeriği kaydetmeyi kısıtla" açıksa forward reddedilir; küçük dosyalar yine indirilip botla gönderilir, büyükler `MAX_FILE_MB` sınırına kadar indirilip yüklenir.
- İlk tarama her kaynak için `SCAN_DEPTH` kadar geriye gider; sonrası artımlıdır.
- Telegram flood limitine takılmamak için `THROTTLE_MS` (varsayılan 350) var.
- Session dosyası hesabının anahtarıdır; paylaşma, sızarsa `logout` et.
