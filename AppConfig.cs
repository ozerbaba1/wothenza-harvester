using System.Globalization;

namespace WothenzaHarvester;

public sealed class AppConfig
{
    public int Port { get; init; } = 10000;

    public string TgApiId { get; init; } = "";
    public string TgApiHash { get; init; } = "";
    public string TgPhone { get; init; } = "";
    public string TgPassword { get; init; } = "";
    public string SessionPath { get; init; } = "data/wz.session";
    public string SessionB64 { get; init; } = "";

    public string BotToken { get; init; } = "";
    public long OwnerChatId { get; init; } = 8821782197;
    public string DestChannel { get; init; } = "wothenza";

    public string DbPath { get; init; } = "data/wz.db";
    public string ArchiveDir { get; init; } = "data/archive";
    public string TempDir { get; init; } = "data/tmp";

    public int ScanIntervalMin { get; init; } = 30;
    public int ScanDepth { get; init; } = 200;
    public int DestDepth { get; init; } = 2000;
    public int MaxPerSource { get; init; } = 2000;
    public int MaxSources { get; init; } = 0;
    public int DmDepth { get; init; } = 100;
    public long MaxFileMb { get; init; } = 400;
    public long ForwardMaxMb { get; init; } = 4000;
    public long ArchiveMaxMb { get; init; } = 100;
    public int ThrottleMs { get; init; } = 350;
    public bool AutoDiscover { get; init; } = true;
    public bool ScanDms { get; init; } = true;
    public bool ScanSaved { get; init; } = true;
    public bool ArchiveFiles { get; init; } = true;
    public bool DryRun { get; init; } = false;

    public string[] Extensions { get; init; } = DefaultExtensions;
    public string[] Keywords { get; init; } = DefaultKeywords;
    public string[] OnlySources { get; init; } = Array.Empty<string>();

    public bool VtEnabled => !string.IsNullOrWhiteSpace(VtApiKey);
    public string VtApiKey { get; init; } = "";
    public bool VtUpload { get; init; } = false;

    public string DashPassword { get; init; } = "";

    public static readonly string[] DefaultExtensions =
    {
        "exe", "zip", "rar", "7z", "apk", "apks", "xapk", "ipa", "tipa", "dmg", "msi",
        "jar", "bin", "elf", "so", "dll", "scr", "bat", "cmd", "ps1", "py", "js",
        "sh", "deb", "rpm", "iso", "img", "tgz", "gz", "tar", "dll", "dat"
    };

    public static readonly string[] DefaultKeywords =
    {
        "cheat", "crack", "hack", "esp", "aimbot", "trigger", "injector", "mod menu",
        "modmenu", "free", "root", "internal", "external", "bypass", "loader", "lisans",
        "license", "hile", "pubg", "bs4", "braintest", "dofamine", "skin", "vip"
    };

    public static AppConfig FromEnv()
    {
        static string S(string k, string d = "") => Environment.GetEnvironmentVariable(k) ?? d;
        static int I(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;
        static long L(string k, long d) => long.TryParse(Environment.GetEnvironmentVariable(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;
        static bool B(string k, bool d) => bool.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
        static string[] A(string k, string[] d)
        {
            var raw = Environment.GetEnvironmentVariable(k);
            if (string.IsNullOrWhiteSpace(raw)) return d;
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(x => x.TrimStart('.').ToLowerInvariant())
                      .ToArray();
        }

        return new AppConfig
        {
            Port = I("PORT", 10000),
            TgApiId = S("TG_API_ID"),
            TgApiHash = S("TG_API_HASH"),
            TgPhone = S("TG_PHONE"),
            TgPassword = S("TG_PASSWORD"),
            SessionPath = S("TG_SESSION_PATH", "data/wz.session"),
            SessionB64 = S("TG_SESSION_B64"),
            BotToken = S("BOT_TOKEN"),
            OwnerChatId = L("OWNER_CHAT_ID", 8821782197),
            DestChannel = S("DEST_CHANNEL", "wothenza"),
            DbPath = S("DB_PATH", "data/wz.db"),
            ArchiveDir = S("ARCHIVE_DIR", "data/archive"),
            TempDir = S("TEMP_DIR", "data/tmp"),
            ScanIntervalMin = I("SCAN_INTERVAL_MIN", 30),
            ScanDepth = I("SCAN_DEPTH", 200),
            DestDepth = I("DEST_DEPTH", 2000),
            MaxPerSource = I("SCAN_MAX_PER_SOURCE", 2000),
            MaxSources = I("SCAN_MAX_SOURCES", 0),
            DmDepth = I("DM_DEPTH", 100),
            MaxFileMb = L("MAX_FILE_MB", 400),
            ForwardMaxMb = L("FORWARD_MAX_MB", 4000),
            ArchiveMaxMb = L("ARCHIVE_MAX_MB", 100),
            ThrottleMs = I("THROTTLE_MS", 350),
            AutoDiscover = B("AUTO_DISCOVER", true),
            ScanDms = B("SCAN_DMS", true),
            ScanSaved = B("SCAN_SAVED", true),
            ArchiveFiles = B("ARCHIVE", true),
            DryRun = B("DRY_RUN", false),
            Extensions = A("EXTENSIONS", DefaultExtensions).Distinct().ToArray(),
            Keywords = A("KEYWORDS", DefaultKeywords).Distinct().ToArray(),
            OnlySources = A("SCAN_ONLY", Array.Empty<string>()),
            VtApiKey = S("VT_API_KEY"),
            VtUpload = B("VT_UPLOAD", false),
            DashPassword = S("DASH_PASSWORD")
        };
    }
}
