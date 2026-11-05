namespace WothenzaHarvester;

public sealed class SchedulerService : BackgroundService
{
    private readonly Db _db;
    private readonly LogBus _log;
    private readonly TelegramUser _tg;

    public SchedulerService(Db db, LogBus log, TelegramUser tg)
    {
        _db = db;
        _log = log;
        _tg = tg;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = _db.DueQueue(DateTimeOffset.UtcNow);
                foreach (var item in due)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    try
                    {
                        var msgId = await _tg.SendPostAsync(item.Channel, item.Text, item.MediaPath, stoppingToken);
                        _db.MarkQueueSent(item.Id, msgId);
                        _log.Add($"post #{item.Id} -> @{item.Channel} msg {msgId}");

                        if (!string.Equals(item.Repeat, "none", StringComparison.OrdinalIgnoreCase))
                        {
                            var delta = item.Repeat.Equals("daily", StringComparison.OrdinalIgnoreCase)
                                ? TimeSpan.FromDays(1)
                                : TimeSpan.FromDays(7);
                            _db.RescheduleQueue(item.Id, DateTimeOffset.UtcNow + delta);
                            _log.Add($"post #{item.Id} tekrarlandı -> {item.Repeat}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _db.MarkQueueError(item.Id, ex.Message);
                        _log.Add($"post #{item.Id} hata: {ex.Message}");
                    }

                    await Task.Delay(1500, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Add("scheduler hata: " + ex.Message);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
