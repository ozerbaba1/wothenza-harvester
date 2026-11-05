using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace WothenzaHarvester;

public sealed class Analyzer
{
    private readonly AppConfig _cfg;
    private readonly LogBus _log;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly SemaphoreSlim _vtGate = new(1, 1);
    private DateTimeOffset _lastVt = DateTimeOffset.MinValue;

    public Analyzer(AppConfig cfg, LogBus log)
    {
        _cfg = cfg;
        _log = log;
    }

    public string Sniff(string path)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            int n;
            using (var fs = File.OpenRead(path))
                n = fs.Read(buffer, 0, buffer.Length);
            if (n < 4) return "EMPTY";

            if (buffer[0] == 0x4D && buffer[1] == 0x5A) return "PE";
            if (buffer[0] == 0x7F && buffer[1] == 0x45 && buffer[2] == 0x4C && buffer[3] == 0x46) return "ELF";
            if (buffer[0] == 0x52 && buffer[1] == 0x61 && buffer[2] == 0x72 && buffer[3] == 0x21) return "RAR";
            if (buffer[0] == 0x37 && buffer[1] == 0x7A && buffer[2] == 0xBC && buffer[3] == 0xAF) return "7Z";
            if (buffer[0] == 0x64 && buffer[1] == 0x65 && buffer[2] == 0x78 && buffer[3] == 0x0A) return "DEX";
            if (buffer[0] == 0xCA && buffer[1] == 0xFE && buffer[2] == 0xBA && buffer[3] == 0xBE) return "MACHO";
            if (buffer[0] == 0xCF && buffer[1] == 0xFA && buffer[2] == 0xED && buffer[3] == 0xFE) return "MACHO";

            if (buffer[0] == 0x50 && buffer[1] == 0x4B && buffer[2] == 0x03 && buffer[3] == 0x04)
            {
                try
                {
                    using var zip = ZipFile.OpenRead(path);
                    if (zip.Entries.Any(e => e.FullName.Equals("AndroidManifest.xml", StringComparison.OrdinalIgnoreCase)))
                        return "APK";
                    if (zip.Entries.Any(e => e.FullName.StartsWith("Payload/", StringComparison.OrdinalIgnoreCase)))
                        return "IPA";
                    return "ZIP";
                }
                catch
                {
                    var head = Encoding.ASCII.GetString(buffer, 0, n);
                    if (head.Contains("AndroidManifest.xml", StringComparison.OrdinalIgnoreCase)) return "APK";
                    if (head.Contains("Payload/", StringComparison.OrdinalIgnoreCase)) return "IPA";
                    return "ZIP";
                }
            }
            return "UNKNOWN";
        }
        catch
        {
            return "UNKNOWN";
        }
    }

    private static string Family(string ext) => ext switch
    {
        "exe" or "dll" or "scr" or "msi" or "sys" or "bin" or "so" or "elf" => "PE",
        "apk" or "apks" or "xapk" => "APK",
        "ipa" or "tipa" => "IPA",
        "zip" or "jar" => "ZIP",
        "rar" => "RAR",
        "7z" => "7Z",
        "dmg" => "DMG",
        "dex" => "DEX",
        _ => "OTHER"
    };

    public (string verdict, string summary) Static(string path, string ext)
    {
        var kind = Sniff(path);
        var fam = Family(ext);
        var mismatch = kind != "UNKNOWN" && fam != "OTHER" && kind != fam;
        var verdict = mismatch ? "suspicious" : (kind == "UNKNOWN" ? "unknown" : "clean");
        var summary = $"magic:{kind} ext:.{ext}" + (mismatch ? " MISMATCH" : "");
        return (verdict, summary);
    }

    public async Task<(string verdict, string? summary)> VirusTotalAsync(string path, string sha256, long size, CancellationToken ct)
    {
        if (!_cfg.VtEnabled) return ("unknown", null);

        await _vtGate.WaitAsync(ct);
        try
        {
            var wait = TimeSpan.FromSeconds(16) - (DateTimeOffset.UtcNow - _lastVt);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastVt = DateTimeOffset.UtcNow;

            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.virustotal.com/api/v3/files/{sha256}");
            req.Headers.Add("x-apikey", _cfg.VtApiKey);
            using var res = await Http.SendAsync(req, ct);

            if (res.IsSuccessStatusCode)
                return ParseStats(await res.Content.ReadAsStringAsync(ct));

            if (res.StatusCode == System.Net.HttpStatusCode.NotFound && _cfg.VtUpload && size <= 32L * 1024 * 1024)
            {
                using var form = new MultipartFormDataContent();
                await using var fs = File.OpenRead(path);
                var sc = new StreamContent(fs);
                form.Add(sc, "file", Path.GetFileName(path));
                using var up = new HttpRequestMessage(HttpMethod.Post, "https://www.virustotal.com/api/v3/files") { Content = form };
                up.Headers.Add("x-apikey", _cfg.VtApiKey);
                using var upRes = await Http.SendAsync(up, ct);
                if (!upRes.IsSuccessStatusCode) return ("unknown", $"VT upload HTTP {(int)upRes.StatusCode}");

                var upJson = await upRes.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(upJson);
                if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("id", out var idEl))
                    return ("unknown", "VT analysis id yok");

                var analysisId = idEl.GetString();
                for (var i = 0; i < 4; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    using var aReq = new HttpRequestMessage(HttpMethod.Get, $"https://www.virustotal.com/api/v3/analyses/{analysisId}");
                    aReq.Headers.Add("x-apikey", _cfg.VtApiKey);
                    using var aRes = await Http.SendAsync(aReq, ct);
                    if (!aRes.IsSuccessStatusCode) continue;
                    var parsed = ParseStats(await aRes.Content.ReadAsStringAsync(ct), analysis: true);
                    if (parsed.verdict != "unknown") return parsed;
                }
                return ("unknown", "VT analizi bekleniyor");
            }

            return ("unknown", $"VT HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex)
        {
            _log.Add($"vt error: {ex.Message}");
            return ("unknown", "VT hata");
        }
        finally
        {
            _vtGate.Release();
        }
    }

    private static (string verdict, string? summary) ParseStats(string json, bool analysis = false)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement stats;
        if (!analysis)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("attributes", out var attrs) ||
                !attrs.TryGetProperty("last_analysis_stats", out stats))
                return ("unknown", "VT stats yok");
        }
        else
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("attributes", out var attrs) ||
                !attrs.TryGetProperty("stats", out stats))
                return ("unknown", null);
        }

        int Get(string n) => stats.TryGetProperty(n, out var v) ? v.GetInt32() : 0;
        var malicious = Get("malicious");
        var suspicious = Get("suspicious");
        var harmless = Get("harmless");
        var undetected = Get("undetected");
        var total = malicious + suspicious + harmless + undetected;
        var verdict = malicious > 0 ? "malicious" : suspicious > 0 ? "suspicious" : "clean";
        return (verdict, $"VT {malicious + suspicious}/{total}");
    }
}
