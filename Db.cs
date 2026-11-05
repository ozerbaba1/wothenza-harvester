using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WothenzaHarvester;

public sealed class Db
{
    private readonly AppConfig _cfg;
    private readonly string _cs;

    public Db(AppConfig cfg)
    {
        _cfg = cfg;
        var dir = Path.GetDirectoryName(Path.GetFullPath(cfg.DbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = cfg.DbPath }.ToString();
        Init();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private void Init()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sources (
                peer_id     INTEGER PRIMARY KEY,
                title       TEXT NOT NULL DEFAULT '',
                username    TEXT NOT NULL DEFAULT '',
                kind        TEXT NOT NULL DEFAULT '',
                last_msg_id INTEGER NOT NULL DEFAULT 0,
                files_found INTEGER NOT NULL DEFAULT 0,
                enabled     INTEGER NOT NULL DEFAULT 1,
                scanned_at  TEXT
            );
            CREATE TABLE IF NOT EXISTS files (
                hash         TEXT PRIMARY KEY,
                source_id    INTEGER NOT NULL,
                source_title TEXT NOT NULL DEFAULT '',
                msg_id       INTEGER NOT NULL,
                doc_id       INTEGER NOT NULL DEFAULT 0,
                file_name    TEXT NOT NULL DEFAULT '',
                ext          TEXT NOT NULL DEFAULT '',
                size         INTEGER NOT NULL DEFAULT 0,
                mime         TEXT NOT NULL DEFAULT '',
                caption      TEXT NOT NULL DEFAULT '',
                verdict      TEXT NOT NULL DEFAULT 'unknown',
                vt_summary   TEXT,
                sent         INTEGER NOT NULL DEFAULT 0,
                archived     INTEGER NOT NULL DEFAULT 0,
                in_dest      INTEGER NOT NULL DEFAULT 0,
                msg_date     TEXT NOT NULL DEFAULT '',
                created_at   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_files_created ON files(created_at DESC);
            CREATE TABLE IF NOT EXISTS queue (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                channel     TEXT NOT NULL,
                text        TEXT NOT NULL DEFAULT '',
                media_path  TEXT,
                schedule_at TEXT NOT NULL,
                repeat      TEXT NOT NULL DEFAULT 'none',
                status      TEXT NOT NULL DEFAULT 'pending',
                sent_msg_id INTEGER,
                error       TEXT,
                sent_at     TEXT
            );
            CREATE TABLE IF NOT EXISTS meta (
                k TEXT PRIMARY KEY,
                v TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        EnsureColumn(c, "files", "doc_id", "doc_id INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(c, "files", "in_dest", "in_dest INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(c, "files", "msg_date", "msg_date TEXT NOT NULL DEFAULT ''");

        using var idx = c.CreateCommand();
        idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_files_dest ON files(in_dest, created_at DESC)";
        idx.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection c, string table, string column, string definition)
    {
        using (var check = c.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var r = check.ExecuteReader();
            while (r.Read())
            {
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }

        using var alter = c.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {definition}";
        alter.ExecuteNonQuery();
    }

    private static string Iso(DateTimeOffset d) => d.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    public void SetMeta(string k, string v)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=$v";
        cmd.Parameters.AddWithValue("$k", k);
        cmd.Parameters.AddWithValue("$v", v);
        cmd.ExecuteNonQuery();
    }

    public string? GetMeta(string k)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT v FROM meta WHERE k=$k";
        cmd.Parameters.AddWithValue("$k", k);
        return cmd.ExecuteScalar() as string;
    }

    // ---------------- sources
    public void UpsertSource(long peerId, string title, string username, string kind)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sources(peer_id,title,username,kind,last_msg_id,files_found,enabled)
            VALUES($id,$t,$u,$k,0,0,1)
            ON CONFLICT(peer_id) DO UPDATE SET title=$t, username=$u, kind=$k
            """;
        cmd.Parameters.AddWithValue("$id", peerId);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.ExecuteNonQuery();
    }

    public List<SourceInfo> ListSources()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT peer_id,title,username,kind,last_msg_id,files_found,enabled,scanned_at FROM sources ORDER BY files_found DESC, title";
        using var r = cmd.ExecuteReader();
        var list = new List<SourceInfo>();
        while (r.Read())
            list.Add(new SourceInfo(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetInt64(4), r.GetInt32(5), r.GetInt64(6) != 0, r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }

    public long GetSourceLast(long peerId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT last_msg_id FROM sources WHERE peer_id=$id";
        cmd.Parameters.AddWithValue("$id", peerId);
        return cmd.ExecuteScalar() is long v ? v : 0;
    }

    public void UpdateSourceProgress(long peerId, long lastMsgId, int foundDelta)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE sources
            SET last_msg_id = CASE WHEN last_msg_id > $last THEN last_msg_id ELSE $last END,
                files_found = files_found + $delta,
                scanned_at = $at
            WHERE peer_id = $id
            """;
        cmd.Parameters.AddWithValue("$last", lastMsgId);
        cmd.Parameters.AddWithValue("$delta", foundDelta);
        cmd.Parameters.AddWithValue("$at", Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", peerId);
        cmd.ExecuteNonQuery();
    }

    // ---------------- files
    public bool FileSeen(string hash)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM files WHERE hash=$h";
        cmd.Parameters.AddWithValue("$h", hash);
        return cmd.ExecuteScalar() != null;
    }

    public bool MsgSeen(long sourceId, int msgId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM files WHERE source_id=$s AND msg_id=$m";
        cmd.Parameters.AddWithValue("$s", sourceId);
        cmd.Parameters.AddWithValue("$m", msgId);
        return cmd.ExecuteScalar() != null;
    }

    public void InsertFile(FileRecord f)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO files
              (hash,source_id,source_title,msg_id,doc_id,file_name,ext,size,mime,caption,verdict,vt_summary,sent,archived,in_dest,msg_date,created_at)
            VALUES($h,$s,$st,$m,$did,$fn,$e,$sz,$mi,$cap,$v,$vt,$sent,$arc,$dest,$md,$at)
            """;
        cmd.Parameters.AddWithValue("$h", f.Hash);
        cmd.Parameters.AddWithValue("$s", f.SourceId);
        cmd.Parameters.AddWithValue("$st", f.SourceTitle);
        cmd.Parameters.AddWithValue("$m", f.MsgId);
        cmd.Parameters.AddWithValue("$did", f.DocId);
        cmd.Parameters.AddWithValue("$fn", f.FileName);
        cmd.Parameters.AddWithValue("$e", f.Ext);
        cmd.Parameters.AddWithValue("$sz", f.Size);
        cmd.Parameters.AddWithValue("$mi", f.Mime);
        cmd.Parameters.AddWithValue("$cap", f.Caption);
        cmd.Parameters.AddWithValue("$v", f.Verdict);
        cmd.Parameters.AddWithValue("$vt", (object?)f.VtSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sent", f.Sent ? 1 : 0);
        cmd.Parameters.AddWithValue("$arc", f.Archived ? 1 : 0);
        cmd.Parameters.AddWithValue("$dest", f.InDest ? 1 : 0);
        cmd.Parameters.AddWithValue("$md", f.MsgDate);
        cmd.Parameters.AddWithValue("$at", f.CreatedAt);
        cmd.ExecuteNonQuery();
    }

    public void MarkFileSent(string hash)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE files SET sent=1 WHERE hash=$h";
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.ExecuteNonQuery();
    }

    public List<FileRecord> ListFiles(string? q, int limit, string? scope = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(q))
            where.Add("(file_name LIKE $q OR source_title LIKE $q OR caption LIKE $q)");
        switch (scope?.ToLowerInvariant())
        {
            case "dest":
                where.Add("in_dest=1");
                break;
            case "new":
                where.Add("in_dest=0");
                break;
            case "sent":
                where.Add("sent=1");
                break;
        }
        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"""
            SELECT hash,source_id,source_title,msg_id,file_name,ext,size,mime,caption,verdict,vt_summary,sent,archived,created_at,doc_id,msg_date,in_dest
            FROM files {clause}
            ORDER BY created_at DESC LIMIT $lim
            """;
        if (!string.IsNullOrWhiteSpace(q)) cmd.Parameters.AddWithValue("$q", $"%{q}%");
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (r.Read())
            list.Add(new FileRecord(
                r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5),
                r.GetInt64(6), r.GetString(7), r.GetString(8), r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10), r.GetInt64(11) != 0, r.GetInt64(12) != 0,
                r.GetString(13), r.IsDBNull(14) ? 0 : r.GetInt64(14), r.GetString(15), r.GetInt64(16) != 0));
        return list;
    }

    public (HashSet<long> Docs, HashSet<string> Signatures) DestIndex()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT doc_id,file_name,size FROM files WHERE in_dest=1";
        using var r = cmd.ExecuteReader();
        var docs = new HashSet<long>();
        var sigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
        {
            if (!r.IsDBNull(0)) docs.Add(r.GetInt64(0));
            sigs.Add(Sig(r.GetString(1), r.GetInt64(2)));
        }
        return (docs, sigs);
    }

    public static string Sig(string fileName, long size) => fileName.Trim().ToLowerInvariant() + "|" + size;

    // ---------------- queue
    public long AddQueue(string channel, string text, string? mediaPath, DateTimeOffset scheduleAt, string repeat)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO queue(channel,text,media_path,schedule_at,repeat,status)
            VALUES($ch,$t,$m,$at,$r,'pending');
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$ch", channel);
        cmd.Parameters.AddWithValue("$t", text);
        cmd.Parameters.AddWithValue("$m", (object?)mediaPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", Iso(scheduleAt));
        cmd.Parameters.AddWithValue("$r", repeat);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public List<QueueItem> ListQueue()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,channel,text,media_path,schedule_at,repeat,status,sent_msg_id,error FROM queue ORDER BY schedule_at DESC LIMIT 200";
        using var r = cmd.ExecuteReader();
        var list = new List<QueueItem>();
        while (r.Read())
            list.Add(new QueueItem(
                r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : r.GetString(8)));
        return list;
    }

    public List<QueueItem> DueQueue(DateTimeOffset now)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id,channel,text,media_path,schedule_at,repeat,status,sent_msg_id,error
            FROM queue WHERE status='pending' AND schedule_at <= $now ORDER BY schedule_at
            """;
        cmd.Parameters.AddWithValue("$now", Iso(now));
        using var r = cmd.ExecuteReader();
        var list = new List<QueueItem>();
        while (r.Read())
            list.Add(new QueueItem(
                r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : r.GetString(8)));
        return list;
    }

    public void MarkQueueSent(long id, long msgId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE queue SET status='sent', sent_msg_id=$m, sent_at=$at, error=NULL WHERE id=$id";
        cmd.Parameters.AddWithValue("$m", msgId);
        cmd.Parameters.AddWithValue("$at", Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkQueueError(long id, string error)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE queue SET status='error', error=$e WHERE id=$id";
        cmd.Parameters.AddWithValue("$e", error);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void RescheduleQueue(long id, DateTimeOffset next)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE queue SET schedule_at=$at, status='pending' WHERE id=$id";
        cmd.Parameters.AddWithValue("$at", Iso(next));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public QueueItem? GetQueue(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,channel,text,media_path,schedule_at,repeat,status,sent_msg_id,error FROM queue WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new QueueItem(
            r.GetInt64(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
            r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : r.GetString(8));
    }

    public bool DeleteQueue(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM queue WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public Stats GetStats()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM sources),
              (SELECT COUNT(*) FROM files),
              (SELECT COUNT(*) FROM files WHERE sent=1),
              (SELECT COUNT(*) FROM files WHERE archived=1),
              (SELECT COUNT(*) FROM files WHERE in_dest=1),
              (SELECT COUNT(*) FROM queue WHERE status='pending'),
              (SELECT COUNT(*) FROM queue WHERE status='sent'),
              (SELECT COALESCE(SUM(size),0) FROM files),
              (SELECT MAX(scanned_at) FROM sources),
              (SELECT MAX(created_at) FROM files)
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return new Stats(
            r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4),
            r.GetInt32(5), r.GetInt32(6), r.GetInt64(7),
            r.IsDBNull(8) ? null : r.GetString(8),
            r.IsDBNull(9) ? null : r.GetString(9));
    }
}
