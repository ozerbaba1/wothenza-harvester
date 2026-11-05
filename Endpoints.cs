using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace WothenzaHarvester;

public static class Endpoints
{
    public sealed record QueueRequest(string Channel, string Text, string ScheduleAt, string? Repeat, string? MediaPath);

    public static void Map(WebApplication app, AppConfig cfg, Db db, LogBus log, ScannerService scanner, TelegramUser tg, BotNotifier bot, Analyzer analyzer)
    {
        app.MapGet("/api/stats", () =>
        {
            var s = db.GetStats();
            return Results.Ok(new
            {
                s.Sources,
                s.Files,
                s.Sent,
                s.Archived,
                s.InDest,
                s.PendingQueue,
                s.SentQueue,
                totalBytes = s.TotalBytes,
                totalHuman = ScannerService.Human(s.TotalBytes),
                s.LastScan,
                s.LastFileAt,
                botConfigured = bot.Configured
            });
        });

        app.MapGet("/api/sources", () => Results.Ok(db.ListSources()));

        app.MapGet("/api/files", (string? q, int? limit, string? scope) =>
            Results.Ok(db.ListFiles(q, Math.Clamp(limit ?? 100, 1, 500), scope)));

        app.MapGet("/api/queue", () => Results.Ok(db.ListQueue()));

        app.MapPost("/api/scan", () =>
        {
            scanner.Trigger();
            return Results.Ok(new { queued = true });
        });

        app.MapPost("/api/queue", (QueueRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Channel) || string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "channel ve text zorunlu" });

            if (!DateTimeOffset.TryParse(req.ScheduleAt, out var when))
                return Results.BadRequest(new { error = "scheduleAt geçersiz (ISO8601)" });

            var repeat = (req.Repeat ?? "none").ToLowerInvariant();
            if (repeat is not ("none" or "daily" or "weekly")) repeat = "none";

            var id = db.AddQueue(req.Channel.Trim().TrimStart('@'), req.Text, req.MediaPath, when, repeat);
            log.Add($"post #{id} kuyruğa eklendi -> @{req.Channel} @ {when:u}");
            return Results.Ok(new { id });
        });

        app.MapPost("/api/queue/{id:long}/run", async (long id, CancellationToken ct) =>
        {
            var item = db.GetQueue(id);
            if (item == null) return Results.NotFound();
            try
            {
                var msgId = await tg.SendPostAsync(item.Channel, item.Text, item.MediaPath, ct);
                db.MarkQueueSent(id, msgId);
                return Results.Ok(new { sent = true, msgId });
            }
            catch (Exception ex)
            {
                db.MarkQueueError(id, ex.Message);
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/queue/{id:long}", (long id) =>
            db.DeleteQueue(id) ? Results.Ok(new { deleted = true }) : Results.NotFound());

        app.MapGet("/api/logs", (int? take) => Results.Ok(log.Recent(Math.Clamp(take ?? 200, 1, 800))));

        app.MapGet("/api/analyze", (string path) =>
        {
            if (!File.Exists(path)) return Results.NotFound(new { error = "dosya yok" });
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var (verdict, summary) = analyzer.Static(path, ext);
            return Results.Ok(new { verdict, summary, size = new FileInfo(path).Length });
        });

        app.MapGet("/api/session", () => Results.Ok(new
        {
            sessionFile = cfg.SessionPath,
            sessionExists = File.Exists(cfg.SessionPath),
            hasSessionB64 = !string.IsNullOrWhiteSpace(cfg.SessionB64),
            apiIdSet = !string.IsNullOrWhiteSpace(cfg.TgApiId),
            apiHashSet = !string.IsNullOrWhiteSpace(cfg.TgApiHash)
        }));
    }
}
