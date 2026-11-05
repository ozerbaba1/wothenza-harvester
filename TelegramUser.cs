using System.Collections.Concurrent;
using TL;

namespace WothenzaHarvester;

public sealed class TelegramUser : IAsyncDisposable
{
    private readonly AppConfig _cfg;
    private readonly LogBus _log;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, InputPeer> _peerCache = new(StringComparer.OrdinalIgnoreCase);
    private WTelegram.Client? _client;

    public TelegramUser(AppConfig cfg, LogBus log)
    {
        _cfg = cfg;
        _log = log;
    }

    public WTelegram.Client Client => _client ?? throw new InvalidOperationException("telegram client bağlı değil");

    public async Task<WTelegram.Client> EnsureAsync(CancellationToken ct = default)
    {
        if (_client?.User != null) return _client;

        await _connectLock.WaitAsync(ct);
        try
        {
            if (_client?.User != null) return _client;

            if (string.IsNullOrWhiteSpace(_cfg.TgApiId) || string.IsNullOrWhiteSpace(_cfg.TgApiHash))
                throw new InvalidOperationException("TG_API_ID / TG_API_HASH eksik");

            if (!File.Exists(_cfg.SessionPath) && string.IsNullOrWhiteSpace(_cfg.SessionB64))
                throw new InvalidOperationException(
                    $"session yok ({_cfg.SessionPath}). önce 'dotnet run -- login' çalıştır, sonra TG_SESSION_B64'ü env'e koy.");

            _client = new WTelegram.Client(Config);
            WTelegram.Helpers.Log = (lvl, str) =>
            {
                if (string.IsNullOrEmpty(str)) return;
                if (str.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || str.Contains("exception", StringComparison.OrdinalIgnoreCase)
                    || str.Contains("flood", StringComparison.OrdinalIgnoreCase)
                    || str.Contains("warning", StringComparison.OrdinalIgnoreCase))
                    _log.Add($"[tg:{lvl}] {str}");
            };

            var me = await _client.LoginUserIfNeeded();
            _log.Add($"telegram user: {me.first_name} @{me.username} id={me.id}");
            return _client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private string Config(string what) => what switch
    {
        "api_id" => _cfg.TgApiId,
        "api_hash" => _cfg.TgApiHash,
        "phone_number" => _cfg.TgPhone,
        "verification_code" => Console.IsInputRedirected ? null! : Prompt("Telegram kod: "),
        "password" => string.IsNullOrEmpty(_cfg.TgPassword)
            ? (Console.IsInputRedirected ? null! : Prompt("2FA şifre: "))
            : _cfg.TgPassword,
        "first_name" => "Wothenza",
        "last_name" => "Harvester",
        "session_pathname" => _cfg.SessionPath,
        "device_model" => "WothenzaHarvester",
        "system_version" => "Windows 10",
        "app_version" => "1.0",
        "system_lang_code" => "tr",
        "lang_code" => "tr",
        _ => null!
    };

    private static string Prompt(string label)
    {
        Console.Write(label);
        return Console.ReadLine() ?? "";
    }

    public async Task<InputPeer> ResolveAsync(string target, CancellationToken ct = default)
    {
        var key = target.Trim();
        if (_peerCache.TryGetValue(key, out var cached)) return cached;

        var name = key
            .Replace("https://t.me/", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://t.me/", "", StringComparison.OrdinalIgnoreCase)
            .Replace("t.me/", "", StringComparison.OrdinalIgnoreCase)
            .TrimStart('@').Trim();

        var client = await EnsureAsync(ct);
        var res = await client.Contacts_ResolveUsername(name);
        InputPeer peer = res.peer switch
        {
            PeerChannel pc => (InputPeer)res.chats[pc.channel_id],
            PeerChat pch => (InputPeer)res.chats[pch.chat_id],
            PeerUser pu => (InputPeer)res.users[pu.user_id],
            _ => throw new InvalidOperationException($"peer çözülemedi: {target}")
        };
        _peerCache[key] = peer;
        return peer;
    }

    public async Task<InputPeer> ResolveAnyAsync(string target, CancellationToken ct = default)
    {
        try
        {
            return await ResolveAsync(target, ct);
        }
        catch
        {
            var name = target.Trim().TrimStart('@');
            var client = await EnsureAsync(ct);
            var dialogs = await client.Messages_GetAllDialogs();
            foreach (var d in dialogs.dialogs)
            {
                var ent = dialogs.UserOrChat(d);
                string title = ent switch
                {
                    User u => TitleOf(u),
                    Channel ch => TitleOf(ch),
                    Chat ch => TitleOf(ch),
                    _ => ""
                };
                if (!title.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                InputPeer peer = ent switch
                {
                    User u => u,
                    Channel ch => ch,
                    Chat ch => ch,
                    _ => throw new InvalidOperationException($"peer çözülemedi: {target}")
                };
                _peerCache[target.Trim()] = peer;
                return peer;
            }
            throw new InvalidOperationException($"kanal bulunamadı: {target}");
        }
    }

    public async Task<long> SendPostAsync(string channel, string text, string? mediaPath, CancellationToken ct = default)
    {
        var client = await EnsureAsync(ct);
        var peer = await ResolveAsync(channel, ct);

        if (!string.IsNullOrWhiteSpace(mediaPath) && File.Exists(mediaPath))
        {
            var input = await client.UploadFileAsync(mediaPath);
            var msg = await client.SendMediaAsync(peer, string.IsNullOrWhiteSpace(text) ? null : text, input);
            return msg.ID;
        }

        var sent = await client.SendMessageAsync(peer, text);
        return sent.ID;
    }

    public async Task SendToSelfAsync(string path, string caption, CancellationToken ct = default)
    {
        var client = await EnsureAsync(ct);
        var input = await client.UploadFileAsync(path);
        await client.SendMediaAsync(InputPeer.Self, caption, input);
    }

    public async Task ForwardToSelfAsync(InputPeer fromPeer, int msgId, CancellationToken ct = default)
    {
        var client = await EnsureAsync(ct);
        await client.Messages_ForwardMessages(
            fromPeer,
            new[] { msgId },
            new[] { WTelegram.Helpers.RandomLong() },
            InputPeer.Self);
    }

    public async Task ResendToSelfAsync(Document doc, string caption, CancellationToken ct = default)
    {
        var client = await EnsureAsync(ct);
        var media = new InputMediaDocument
        {
            id = new InputDocument
            {
                id = doc.id,
                access_hash = doc.access_hash,
                file_reference = doc.file_reference
            }
        };
        await client.SendMessageAsync(InputPeer.Self, caption, media);
    }

    public async Task DownloadHeadAsync(Document doc, string path, int bytes, CancellationToken ct = default)
    {
        var client = await EnsureAsync(ct);
        var loc = new InputDocumentFileLocation
        {
            id = doc.id,
            access_hash = doc.access_hash,
            file_reference = doc.file_reference,
            thumb_size = ""
        };

        Upload_FileBase? res;
        try
        {
            res = await client.Upload_GetFile(loc, 0, bytes, precise: true, cdn_supported: false);
        }
        catch (Exception ex)
        {
            if (!TryMigrateDc(ex, out var dc)) throw;
            _log.Add($"dosya DC{dc}'de, oraya bağlanılıyor");
            var dcClient = await client.GetClientForDC(dc, connect: true);
            res = await dcClient.Upload_GetFile(loc, 0, bytes, precise: true, cdn_supported: false);
        }

        if (res is not Upload_File file)
            throw new InvalidOperationException("kısmi indirme başarısız");

        await using var fs = File.Create(path);
        await fs.WriteAsync(file.bytes);
    }

    private static bool TryMigrateDc(Exception ex, out int dc)
    {
        dc = 0;
        if (ex is RpcException rpc &&
            rpc.Message.Contains("FILE_MIGRATE", StringComparison.OrdinalIgnoreCase) &&
            rpc.X > 0)
        {
            dc = rpc.X;
            return true;
        }

        var msg = ex.Message ?? "";
        var idx = msg.IndexOf("FILE_MIGRATE_", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var digits = new string(msg[(idx + 13)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out dc);
    }

    public static string TitleOf(object? ent) => ent switch
    {
        Channel ch => ch.title ?? "",
        Chat ch => ch.title ?? "",
        User u => $"{u.first_name} {u.last_name}".Trim(),
        _ => ""
    };

    public static string UsernameOf(object? ent) => ent switch
    {
        Channel ch => ch.username ?? "",
        User u => u.username ?? "",
        _ => ""
    };

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
        _connectLock.Dispose();
    }
}
