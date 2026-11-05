using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace WothenzaHarvester;

public sealed class BotNotifier
{
    private readonly AppConfig _cfg;
    private readonly LogBus _log;
    private readonly TelegramBotClient? _bot;

    public BotNotifier(AppConfig cfg, LogBus log)
    {
        _cfg = cfg;
        _log = log;
        if (!string.IsNullOrWhiteSpace(cfg.BotToken))
            _bot = new TelegramBotClient(cfg.BotToken);
    }

    public bool Configured => _bot != null;

    public async Task<string?> GetMeAsync(CancellationToken ct = default)
    {
        if (_bot == null) return null;
        try
        {
            var me = await _bot.GetMe(ct);
            return "@" + me.Username;
        }
        catch (Exception ex)
        {
            _log.Add("bot getMe hata: " + ex.Message);
            return null;
        }
    }

    public async Task<bool> SendFileAsync(string path, string fileName, string caption, CancellationToken ct = default)
    {
        if (_bot == null) return false;
        try
        {
            await using var fs = File.OpenRead(path);
            var input = InputFile.FromStream(fs, fileName);
            await _bot.SendDocument(
                chatId: _cfg.OwnerChatId,
                document: input,
                caption: caption,
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.Add($"bot dosya hata ({fileName}): {ex.Message}");
            return false;
        }
    }

    public async Task SendTextAsync(string html, CancellationToken ct = default)
    {
        if (_bot == null) return;
        try
        {
            await _bot.SendMessage(
                chatId: _cfg.OwnerChatId,
                text: html,
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.Add("bot mesaj hata: " + ex.Message);
        }
    }
}
