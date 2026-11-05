using WothenzaHarvester;
using TL;

var cfg = AppConfig.FromEnv();
BootstrapSession(cfg);
EnsureDirs(cfg);

if (args.Length > 0)
{
    switch (args[0].ToLowerInvariant())
    {
        case "login":
            return await LoginAsync(cfg);
        case "login-server":
            return await LoginServerAsync(cfg);
        case "export-session":
            return ExportSession(cfg);
        case "scan":
            return await ScanOnceAsync(cfg);
        case "whoami":
            return await WhoAmIAsync(cfg);
        case "bottest":
            return await BotTestAsync(cfg);
        case "probe":
            return await ProbeAsync(cfg, args.ElementAtOrDefault(1) ?? cfg.DestChannel);
        case "web":
            break;
        default:
            Console.Error.WriteLine("komutlar: login | export-session | scan | web");
            return 2;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{cfg.Port}");

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<LogBus>();
builder.Services.AddSingleton<TelegramUser>();
builder.Services.AddSingleton<BotNotifier>();
builder.Services.AddSingleton<Analyzer>();
builder.Services.AddSingleton<ScannerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScannerService>());
builder.Services.AddHostedService<SchedulerService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

if (!string.IsNullOrWhiteSpace(cfg.DashPassword))
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            var key = ctx.Request.Headers["x-dash-key"].FirstOrDefault()
                      ?? ctx.Request.Query["key"].FirstOrDefault();
            if (!string.Equals(key, cfg.DashPassword, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                return;
            }
        }
        await next();
    });
}

app.MapGet("/healthz", () => Results.Ok(new { ok = true, time = DateTimeOffset.UtcNow }));
app.MapGet("/api/ping", () => Results.Ok(new { pong = true }));

var db = app.Services.GetRequiredService<Db>();
var log = app.Services.GetRequiredService<LogBus>();
var scanner = app.Services.GetRequiredService<ScannerService>();
var tg = app.Services.GetRequiredService<TelegramUser>();
var bot = app.Services.GetRequiredService<BotNotifier>();
var analyzer = app.Services.GetRequiredService<Analyzer>();

Endpoints.Map(app, cfg, db, log, scanner, tg, bot, analyzer);

_ = Task.Run(async () =>
{
    try
    {
        var me = await bot.GetMeAsync();
        log.Add(me == null ? "bot yapılandırılmadı" : $"bot hazır: {me}");
    }
    catch { }
});

log.Add($"web başlıyor :{cfg.Port}");
await app.RunAsync();
return 0;

static void EnsureDirs(AppConfig cfg)
{
    foreach (var dir in new[] { cfg.ArchiveDir, cfg.TempDir, Path.GetDirectoryName(Path.GetFullPath(cfg.DbPath)) ?? "." })
        Directory.CreateDirectory(dir);
}

static void BootstrapSession(AppConfig cfg)
{
    if (string.IsNullOrWhiteSpace(cfg.SessionB64)) return;
    var dir = Path.GetDirectoryName(Path.GetFullPath(cfg.SessionPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    try
    {
        File.WriteAllBytes(cfg.SessionPath, Convert.FromBase64String(cfg.SessionB64));
    }
    catch (FormatException)
    {
        Console.Error.WriteLine("TG_SESSION_B64 geçersiz base64, yok sayıldı");
    }
}

static string Prompt(string label)
{
    Console.Write(label);
    return Console.ReadLine() ?? "";
}

static async Task<int> LoginAsync(AppConfig cfg)
{
    if (string.IsNullOrWhiteSpace(cfg.TgApiId) || string.IsNullOrWhiteSpace(cfg.TgApiHash))
    {
        Console.Error.WriteLine("TG_API_ID / TG_API_HASH yok");
        return 2;
    }
    if (string.IsNullOrWhiteSpace(cfg.TgPhone))
    {
        Console.Error.WriteLine("TG_PHONE yok");
        return 2;
    }

    var dir = Path.GetDirectoryName(Path.GetFullPath(cfg.SessionPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

    using var client = new WTelegram.Client(what => what switch
    {
        "api_id" => cfg.TgApiId,
        "api_hash" => cfg.TgApiHash,
        "phone_number" => cfg.TgPhone,
        "verification_code" => Prompt("Telegram kod: "),
        "password" => string.IsNullOrEmpty(cfg.TgPassword) ? Prompt("2FA şifre: ") : cfg.TgPassword,
        "first_name" => "Wothenza",
        "last_name" => "Harvester",
        "session_pathname" => cfg.SessionPath,
        _ => null!
    });

    var me = await client.LoginUserIfNeeded();
    Console.WriteLine($"ok: {me.first_name} @{me.username} id={me.id}");
    Console.WriteLine($"session: {cfg.SessionPath}");
    Console.WriteLine("render için: dotnet run -- export-session");
    return 0;
}

static async Task<int> LoginServerAsync(AppConfig cfg)
{
    if (string.IsNullOrWhiteSpace(cfg.TgApiId) || string.IsNullOrWhiteSpace(cfg.TgApiHash) ||
        string.IsNullOrWhiteSpace(cfg.TgPhone))
    {
        Console.Error.WriteLine("TG_API_ID / TG_API_HASH / TG_PHONE eksik");
        return 2;
    }

    var dir = Path.GetDirectoryName(Path.GetFullPath(cfg.SessionPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

    var codeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var passTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.UseUrls("http://127.0.0.1:10098");
    var web = builder.Build();

    web.MapPost("/code", async (HttpRequest req) =>
    {
        using var sr = new StreamReader(req.Body);
        var body = (await sr.ReadToEndAsync()).Trim();
        codeTcs.TrySetResult(body);
        return Results.Ok(new { ok = true });
    });
    web.MapPost("/password", async (HttpRequest req) =>
    {
        using var sr = new StreamReader(req.Body);
        var body = (await sr.ReadToEndAsync()).Trim();
        passTcs.TrySetResult(body);
        return Results.Ok(new { ok = true });
    });
    web.MapGet("/status", () => Results.Ok(new
    {
        waitingCode = !codeTcs.Task.IsCompleted,
        waitingPassword = !passTcs.Task.IsCompleted
    }));

    await web.StartAsync();
    Console.WriteLine("login-server http://127.0.0.1:10098  (POST /code, /password)");

    WTelegram.Client? client = null;
    try
    {
        client = new WTelegram.Client(what => what switch
        {
            "api_id" => cfg.TgApiId,
            "api_hash" => cfg.TgApiHash,
            "phone_number" => cfg.TgPhone,
            "verification_code" => codeTcs.Task.GetAwaiter().GetResult(),
            "password" => passTcs.Task.GetAwaiter().GetResult(),
            "first_name" => "Wothenza",
            "last_name" => "Harvester",
            "session_pathname" => cfg.SessionPath,
            _ => null!
        });

        var me = await client.LoginUserIfNeeded();
        Console.WriteLine($"LOGIN_OK id={me.id} username=@{me.username}");

        client.Dispose();
        client = null;

        var b64 = Convert.ToBase64String(File.ReadAllBytes(cfg.SessionPath));
        await File.WriteAllTextAsync("session.b64", b64);
        Console.WriteLine($"SESSION_B64_FILE={Path.GetFullPath("session.b64")}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("login hata: " + ex.Message);
        return 1;
    }
    finally
    {
        client?.Dispose();
        await web.StopAsync();
    }
}

static int ExportSession(AppConfig cfg)
{
    if (!File.Exists(cfg.SessionPath))
    {
        Console.Error.WriteLine("session dosyası yok: " + cfg.SessionPath);
        return 2;
    }
    var b64 = Convert.ToBase64String(File.ReadAllBytes(cfg.SessionPath));
    Console.Error.WriteLine("TG_SESSION_B64 olarak env'e koy:");
    Console.WriteLine(b64);
    return 0;
}

static async Task<int> ProbeAsync(AppConfig cfg, string channel)
{
    var log = new LogBus();
    var tg = new TelegramUser(cfg, log);
    var analyzer = new Analyzer(cfg, log);
    Directory.CreateDirectory(cfg.TempDir);

    try
    {
        var client = await tg.EnsureAsync();
        var peer = await tg.ResolveAnyAsync(channel);
        var hist = await client.Messages_GetHistory(peer, limit: 100);

        Message? best = null;
        Document? bestDoc = null;
        foreach (var mb in hist.Messages)
        {
            if (mb is not Message m || m.media is not MessageMediaDocument mmd || mmd.document is not Document d) continue;
            if (bestDoc == null || d.size > bestDoc.size) { best = m; bestDoc = d; }
        }

        if (best == null || bestDoc == null)
        {
            Console.WriteLine("dosya bulunamadı");
            return 1;
        }

        var fname = bestDoc.attributes?.OfType<DocumentAttributeFilename>().FirstOrDefault()?.file_name ?? $"file_{best.ID}";
        var ext = Path.GetExtension(fname).TrimStart('.').ToLowerInvariant();
        var head = Path.Combine(cfg.TempDir, "probe_head.bin");
        try
        {
            await tg.DownloadHeadAsync(bestDoc, head, 64 * 1024);
            var (verdict, summary) = analyzer.Static(head, ext);
            Console.WriteLine($"file={fname}");
            Console.WriteLine($"size={bestDoc.size} date={best.date:u} msg={best.ID}");
            Console.WriteLine($"head={new FileInfo(head).Length} bytes verdict={verdict} summary={summary}");
        }
        finally
        {
            try { File.Delete(head); } catch { }
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("probe hata: " + ex.Message);
        return 1;
    }
    finally
    {
        await tg.DisposeAsync();
    }
}

static async Task<int> BotTestAsync(AppConfig cfg)
{
    var log = new LogBus();
    var bot = new BotNotifier(cfg, log);
    if (!bot.Configured)
    {
        Console.Error.WriteLine("BOT_TOKEN yok");
        return 2;
    }

    var me = await bot.GetMeAsync();
    if (me == null)
    {
        Console.Error.WriteLine("bot getMe başarısız (token geçersiz veya ağ yok)");
        return 1;
    }

    Console.WriteLine($"bot ok: {me} -> owner {cfg.OwnerChatId}");
    await bot.SendTextAsync($"✅ <b>Wothenza Harvester</b> bağlandı.\nBot: {me}\nOwner: <code>{cfg.OwnerChatId}</code>");
    Console.WriteLine("test mesajı gönderildi");
    return 0;
}

static async Task<int> WhoAmIAsync(AppConfig cfg)
{
    var log = new LogBus();
    var tg = new TelegramUser(cfg, log);
    try
    {
        var client = await tg.EnsureAsync();
        Console.WriteLine($"ok id={client.User.id} username=@{client.User.username} name={client.User.first_name}");
        var dialogs = await client.Messages_GetAllDialogs();
        Console.WriteLine($"dialogs={dialogs.dialogs.Length}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("whoami hata: " + ex.Message);
        return 1;
    }
    finally
    {
        await tg.DisposeAsync();
    }
}

static async Task<int> ScanOnceAsync(AppConfig cfg)
{
    var log = new LogBus();
    var db = new Db(cfg);
    var tg = new TelegramUser(cfg, log);
    var bot = new BotNotifier(cfg, log);
    var analyzer = new Analyzer(cfg, log);
    var scanner = new ScannerService(cfg, db, log, tg, bot, analyzer);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    try
    {
        await scanner.RunOnceAsync(cts.Token);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("scan hata: " + ex.Message);
        return 1;
    }
    finally
    {
        await tg.DisposeAsync();
    }
}
