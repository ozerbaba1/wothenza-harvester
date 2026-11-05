using System.Net;
using System.Security.Cryptography;
using System.Text;
using TL;

namespace WothenzaHarvester;

public sealed class ScannerService : BackgroundService
{
    private readonly AppConfig _cfg;
    private readonly Db _db;
    private readonly LogBus _log;
    private readonly TelegramUser _tg;
    private readonly BotNotifier _bot;
    private readonly Analyzer _analyzer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0);

    public ScannerService(AppConfig cfg, Db db, LogBus log, TelegramUser tg, BotNotifier bot, Analyzer analyzer)
    {
        _cfg = cfg;
        _db = db;
        _log = log;
        _tg = tg;
        _bot = bot;
        _analyzer = analyzer;
    }

    public void Trigger()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Add("scan hata: " + ex.Message);
            }

            try
            {
                var delay = Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _cfg.ScanIntervalMin)), stoppingToken);
                var wake = _signal.WaitAsync(stoppingToken);
                await Task.WhenAny(delay, wake);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            _log.Add("scan zaten çalışıyor, atlandı");
            return;
        }

        try
        {
            var client = await _tg.EnsureAsync(ct);
            _log.Add("scan başladı");
            var dialogs = await client.Messages_GetAllDialogs();

            var destName = _cfg.DestChannel.Trim().TrimStart('@');
            var targets = new List<(InputPeer Peer, long Id, string Title, string Username, string Kind, int Depth, bool IsDest)>();
            foreach (Dialog d in dialogs.dialogs)
            {
                var ent = dialogs.UserOrChat(d);
                switch (ent)
                {
                    case User u when u.id == client.User.id:
                        if (_cfg.ScanSaved)
                            targets.Add((InputPeer.Self, u.id, "Saved Messages", "", "saved", 500, false));
                        break;

                    case User u:
                        if (_cfg.ScanDms)
                            targets.Add(((InputPeer)u, u.id, TelegramUser.TitleOf(u), TelegramUser.UsernameOf(u), "dm", _cfg.DmDepth, false));
                        break;

                    case Channel ch:
                    {
                        var title = TelegramUser.TitleOf(ch);
                        var username = TelegramUser.UsernameOf(ch);
                        var isDest = !string.IsNullOrEmpty(destName) &&
                                     (username.Equals(destName, StringComparison.OrdinalIgnoreCase) ||
                                      title.Equals(destName, StringComparison.OrdinalIgnoreCase));
                        targets.Add(((InputPeer)ch, ch.id, title, username, "channel",
                            isDest ? _cfg.DestDepth : _cfg.ScanDepth, isDest));
                        break;
                    }

                    case Chat ch:
                        targets.Add(((InputPeer)ch, ch.id, TelegramUser.TitleOf(ch), "", "group", _cfg.ScanDepth, false));
                        break;
                }
            }

            if (_cfg.OnlySources.Length > 0)
            {
                targets = targets
                    .Where(t => _cfg.OnlySources.Any(f =>
                        t.Username.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                        t.Title.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                _log.Add($"kaynak filtresi: {string.Join(", ", _cfg.OnlySources)} -> {targets.Count} kaynak");
            }

            if (_cfg.MaxSources > 0 && targets.Count > _cfg.MaxSources)
            {
                targets = targets.Take(_cfg.MaxSources).ToList();
                _log.Add($"kaynak limiti: {targets.Count} (SCAN_MAX_SOURCES)");
            }

            targets = targets.OrderBy(t => t.IsDest ? 0 : 1).ToList();
            var (destDocs, destSigs) = _db.DestIndex();
            if (destDocs.Count > 0 || destSigs.Count > 0)
                _log.Add($"kanalda bilinen: {destDocs.Count} dosya kimliği, {destSigs.Count} imza");

            _log.Add($"{targets.Count} kaynak taranacak");
            var totalFiles = 0;

            foreach (var t in targets)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    _db.UpsertSource(t.Id, t.Title, t.Username, t.Kind);
                    var last = _db.GetSourceLast(t.Id);
                    var found = await ScanSourceAsync(client, t.Peer, t.Id, t.Title, t.Username, t.Depth, last,
                        t.IsDest, destDocs, destSigs, ct);
                    totalFiles += found;
                    if (found > 0)
                        _log.Add(t.IsDest
                            ? $"kanal '{t.Title}': {found} dosya indekslendi (kanalda)"
                            : $"{t.Kind} '{t.Title}': +{found} dosya");
                }
                catch (Exception ex)
                {
                    _log.Add($"kaynak hata '{t.Title}': {ex.Message}");
                }

                await Task.Delay(400, ct);
            }

            _db.SetMeta("last_scan", DateTimeOffset.UtcNow.ToString("o"));
            _log.Add($"scan bitti, {totalFiles} yeni dosya");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> ScanSourceAsync(
        WTelegram.Client client, InputPeer peer, long sourceId, string sourceTitle,
        string username, int depth, long last, bool isDest,
        HashSet<long> destDocs, HashSet<string> destSigs, CancellationToken ct)
    {
        var limit = last == 0 ? depth : _cfg.MaxPerSource;
        var offsetId = 0;
        var fetched = 0;
        var found = 0;
        var maxId = last;

        while (fetched < limit && !ct.IsCancellationRequested)
        {
            var batch = Math.Clamp(limit - fetched, 1, 100);
            var hist = await client.Messages_GetHistory(peer, offset_id: offsetId, min_id: (int)last, limit: batch);
            if (hist.Messages.Length == 0) break;

            foreach (var mb in hist.Messages)
            {
                if (mb is not Message m) continue;
                if (m.ID > maxId) maxId = m.ID;
                if (await ProcessMessageAsync(client, peer, m, sourceId, sourceTitle, username,
                        isDest, destDocs, destSigs, ct))
                {
                    found++;
                    await Task.Delay(_cfg.ThrottleMs, ct);
                }
            }

            fetched += hist.Messages.Length;
            offsetId = hist.Messages[^1].ID;
            if (hist.Messages.Length < batch) break;
        }

        _db.UpdateSourceProgress(sourceId, maxId, found);
        return found;
    }

    private async Task<bool> ProcessMessageAsync(
        WTelegram.Client client, InputPeer peer, Message m, long sourceId, string sourceTitle, string username,
        bool isDest, HashSet<long> destDocs, HashSet<string> destSigs, CancellationToken ct)
    {
        if (m.media is not MessageMediaDocument mmd || mmd.document is not Document doc) return false;

        var fname = doc.attributes?.OfType<DocumentAttributeFilename>().FirstOrDefault()?.file_name;
        if (string.IsNullOrWhiteSpace(fname)) fname = $"file_{m.ID}";
        var ext = Path.GetExtension(fname).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = GuessExt(doc.mime_type);
        if (string.IsNullOrEmpty(ext) || !_cfg.Extensions.Contains(ext)) return false;

        var size = doc.size;
        if (size <= 0) return false;
        var msgDate = MsgDateOf(m);

        if (isDest)
            return RecordDestAsync(m, doc, fname, ext, size, sourceId, sourceTitle, msgDate, destDocs, destSigs);

        if (_db.MsgSeen(sourceId, m.ID)) return false;

        var sig = Db.Sig(fname, size);
        if (destDocs.Contains(doc.id) || destSigs.Contains(sig))
            return RecordDestDuplicateAsync(m, doc, fname, ext, size, sourceId, sourceTitle, msgDate, destDocs, destSigs);

        if (size > _cfg.MaxFileMb * 1024 * 1024)
        {
            if (size > _cfg.ForwardMaxMb * 1024 * 1024)
            {
                _log.Add($"atlandı (boyut {Human(size)} > {_cfg.ForwardMaxMb} MB): {fname}");
                return false;
            }
            return await ForwardLargeAsync(peer, m, doc, fname, ext, size, sourceId, sourceTitle, username, ct);
        }

        Directory.CreateDirectory(_cfg.TempDir);
        var tmp = Path.Combine(_cfg.TempDir, $"{sourceId}_{m.ID}_{Sanitize(fname)}");
        try
        {
            await using (var fs = File.Create(tmp))
                await client.DownloadFileAsync(doc, fs, progress: null);
        }
        catch (Exception ex)
        {
            _log.Add($"indirme hata ({fname}): {ex.Message}");
            TryDelete(tmp);
            return false;
        }

        try
        {
            var hash = await Sha256FileAsync(tmp, ct);
            if (_db.FileSeen(hash))
            {
                TryDelete(tmp);
                return false;
            }

            var (verdict, summary) = _analyzer.Static(tmp, ext);
            if (_cfg.VtEnabled)
            {
                var vt = await _analyzer.VirusTotalAsync(tmp, hash, size, ct);
                if (vt.verdict != "unknown")
                {
                    verdict = vt.verdict;
                    summary = vt.summary;
                }
            }

            var caption = BuildCaption(fname, sourceTitle, username, size, hash, verdict, summary, m.ID, msgDate);
            var sent = false;

            if (_cfg.DryRun)
            {
                _log.Add($"🧪 dry-run: {fname} [{Human(size)}] {verdict}");
            }
            else if (size <= 45L * 1024 * 1024)
            {
                sent = await _bot.SendFileAsync(tmp, fname, caption, ct);
                if (!sent)
                {
                    try
                    {
                        await _tg.SendToSelfAsync(tmp, caption, ct);
                        await _bot.SendTextAsync($"⬆️ Bot teslim edemedi, userbot Saved Messages'a yükledi: <b>{WebUtility.HtmlEncode(fname)}</b> ({Human(size)})", ct);
                        sent = true;
                    }
                    catch (Exception ex)
                    {
                        _log.Add($"fallback yükleme hata: {ex.Message}");
                    }
                }
            }
            else
            {
                try
                {
                    await _tg.SendToSelfAsync(tmp, caption, ct);
                    await _bot.SendTextAsync($"⬆️ Büyük dosya Saved Messages'a yüklendi: <b>{WebUtility.HtmlEncode(fname)}</b> ({Human(size)})", ct);
                    sent = true;
                }
                catch (Exception ex)
                {
                    _log.Add($"büyük dosya yükleme hata: {ex.Message}");
                }
            }

            var archived = false;
            if (_cfg.ArchiveFiles && size <= _cfg.ArchiveMaxMb * 1024 * 1024)
            {
                Directory.CreateDirectory(_cfg.ArchiveDir);
                var dest = Path.Combine(_cfg.ArchiveDir, hash[..16] + "." + ext);
                File.Copy(tmp, dest, overwrite: true);
                archived = true;
            }

            _db.InsertFile(new FileRecord(
                hash, sourceId, sourceTitle, m.ID, fname, ext, size, doc.mime_type ?? "",
                m.message ?? "", verdict, summary, sent, archived,
                DateTimeOffset.UtcNow.ToString("o"), doc.id, msgDate, false));

            _log.Add($"{(sent ? "✓" : "•")} {fname} [{Human(size)}] {verdict} {summary}");
            return true;
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private bool RecordDestAsync(
        Message m, Document doc, string fname, string ext, long size,
        long sourceId, string sourceTitle, string msgDate,
        HashSet<long> destDocs, HashSet<string> destSigs)
    {
        var hash = "doc:" + doc.id.ToString("x16");
        destDocs.Add(doc.id);
        destSigs.Add(Db.Sig(fname, size));

        if (_db.MsgSeen(sourceId, m.ID) || _db.FileSeen(hash)) return false;

        _db.InsertFile(new FileRecord(
            hash, sourceId, sourceTitle, m.ID, fname, ext, size, doc.mime_type ?? "",
            m.message ?? "", "in_dest", "kanalda mevcut", false, false,
            DateTimeOffset.UtcNow.ToString("o"), doc.id, msgDate, true));
        return true;
    }

    private bool RecordDestDuplicateAsync(
        Message m, Document doc, string fname, string ext, long size,
        long sourceId, string sourceTitle, string msgDate,
        HashSet<long> destDocs, HashSet<string> destSigs)
    {
        var hash = "doc:" + doc.id.ToString("x16");
        destDocs.Add(doc.id);
        destSigs.Add(Db.Sig(fname, size));

        if (_db.MsgSeen(sourceId, m.ID) || _db.FileSeen(hash)) return false;

        _db.InsertFile(new FileRecord(
            hash, sourceId, sourceTitle, m.ID, fname, ext, size, doc.mime_type ?? "",
            m.message ?? "", "in_dest", "kanalda zaten var", false, false,
            DateTimeOffset.UtcNow.ToString("o"), doc.id, msgDate, true));

        _log.Add($"⏭ kanalda var: {fname} [{Human(size)}]");
        return true;
    }

    private static string MsgDateOf(Message m) =>
        new DateTimeOffset(DateTime.SpecifyKind(m.date, DateTimeKind.Utc)).UtcDateTime.ToString("o");

    private async Task<bool> ForwardLargeAsync(
        InputPeer peer, Message m, Document doc, string fname, string ext, long size,
        long sourceId, string sourceTitle, string username, CancellationToken ct)
    {
        if (_db.MsgSeen(sourceId, m.ID)) return false;

        var hash = "doc:" + doc.id.ToString("x16");
        if (_db.FileSeen(hash)) return false;

        var msgDate = MsgDateOf(m);
        var verdict = "unknown";
        var summary = "header alınamadı";
        Directory.CreateDirectory(_cfg.TempDir);
        var headPath = Path.Combine(_cfg.TempDir, $"head_{sourceId}_{m.ID}.bin");
        try
        {
            await _tg.DownloadHeadAsync(doc, headPath, 64 * 1024, ct);
            var st = _analyzer.Static(headPath, ext);
            verdict = st.verdict;
            summary = "partial " + st.summary;
        }
        catch (Exception ex)
        {
            _log.Add($"header analiz hata ({fname}): {ex.Message}");
        }
        finally
        {
            TryDelete(headPath);
        }

        if (_cfg.DryRun)
        {
            _db.InsertFile(new FileRecord(
                hash, sourceId, sourceTitle, m.ID, fname, ext, size, doc.mime_type ?? "",
                m.message ?? "", verdict, summary + " (dry-run)", false, false,
                DateTimeOffset.UtcNow.ToString("o"), doc.id, msgDate, false));
            _log.Add($"🧪 dry-run büyük: {fname} [{Human(size)}] {verdict}");
            return true;
        }

        try
        {
            var transport = "forward";
            try
            {
                await _tg.ForwardToSelfAsync(peer, m.ID, ct);
            }
            catch (Exception fwdEx)
            {
                try
                {
                    await _tg.ResendToSelfAsync(doc, BuildPlainCaption(fname, sourceTitle, username, size), ct);
                    transport = "resend";
                }
                catch (Exception reEx)
                {
                    _log.Add($"forward/resend hata ({fname}): {fwdEx.Message} / {reEx.Message}");
                    return false;
                }
            }

            await _bot.SendTextAsync(
                $"📦 <b>{WebUtility.HtmlEncode(fname)}</b>\n" +
                $"📡 {WebUtility.HtmlEncode(sourceTitle)}" +
                (string.IsNullOrEmpty(username) ? "" : $" (@{username})") + "\n" +
                $"🕓 {FormatDate(msgDate)}\n" +
                $"📏 {Human(size)} · 🧠 {WebUtility.HtmlEncode(verdict)}" +
                (string.IsNullOrEmpty(summary) ? "" : $" · {WebUtility.HtmlEncode(summary)}") + "\n" +
                $"♻️ büyük dosya Saved Messages'a aktarıldı ({transport})", ct);

            _db.InsertFile(new FileRecord(
                hash, sourceId, sourceTitle, m.ID, fname, ext, size, doc.mime_type ?? "",
                m.message ?? "", verdict, summary + $" ({transport})", true, false,
                DateTimeOffset.UtcNow.ToString("o"), doc.id, msgDate, false));

            _log.Add($"♻️ {fname} [{Human(size)}] {verdict} ({transport})");
            return true;
        }
        catch (Exception ex)
        {
            _log.Add($"forward hata ({fname}): {ex.Message}");
            return false;
        }
    }

    private static string BuildPlainCaption(string fname, string source, string username, long size)
    {
        var who = string.IsNullOrEmpty(username) ? source : $"{source} (@{username})";
        return $"📦 {fname}\n📡 {who}\n📏 {Human(size)}\n♻️ büyük dosya — Saved Messages";
    }

    private static string BuildCaption(string fname, string source, string username, long size, string hash,
        string verdict, string? summary, int msgId, string msgDate)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"📦 <b>{WebUtility.HtmlEncode(fname)}</b>");
        sb.AppendLine($"📡 {WebUtility.HtmlEncode(source)}" + (string.IsNullOrEmpty(username) ? "" : $" (@{username})"));
        sb.AppendLine($"🕓 {FormatDate(msgDate)}");
        sb.AppendLine($"📏 {Human(size)}");
        sb.AppendLine($"🧠 {WebUtility.HtmlEncode(verdict)}" + (string.IsNullOrEmpty(summary) ? "" : $" · {WebUtility.HtmlEncode(summary)}"));
        sb.AppendLine($"🔐 <code>{hash[..16]}</code> · msg {msgId}");
        return sb.ToString().TrimEnd();
    }

    private static string FormatDate(string iso) =>
        DateTimeOffset.TryParse(iso, out var d) ? d.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'") : iso;

    private static string GuessExt(string? mime) => mime switch
    {
        "application/vnd.android.package-archive" => "apk",
        "application/zip" => "zip",
        "application/x-msdownload" => "exe",
        "application/x-msdos-program" => "exe",
        "application/x-rar-compressed" => "rar",
        "application/x-7z-compressed" => "7z",
        "application/java-archive" => "jar",
        _ => ""
    };

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.Length > 120 ? sb.ToString(0, 120) : sb.ToString();
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static string Human(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }
}
