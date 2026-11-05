namespace WothenzaHarvester;

public sealed record SourceInfo(
    long PeerId,
    string Title,
    string Username,
    string Kind,
    long LastMsgId,
    int FilesFound,
    bool Enabled,
    string? ScannedAt);

public sealed record FileRecord(
    string Hash,
    long SourceId,
    string SourceTitle,
    int MsgId,
    string FileName,
    string Ext,
    long Size,
    string Mime,
    string Caption,
    string Verdict,
    string? VtSummary,
    bool Sent,
    bool Archived,
    string CreatedAt,
    long DocId,
    string MsgDate,
    bool InDest);

public sealed record QueueItem(
    long Id,
    string Channel,
    string Text,
    string? MediaPath,
    string ScheduleAt,
    string Repeat,
    string Status,
    long? SentMsgId,
    string? Error);

public sealed record Stats(
    int Sources,
    int Files,
    int Sent,
    int Archived,
    int InDest,
    int PendingQueue,
    int SentQueue,
    long TotalBytes,
    string? LastScan,
    string? LastFileAt);
