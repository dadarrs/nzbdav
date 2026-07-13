namespace NzbWebDAV.Database.Models.Metrics;

/// <summary>
/// Per-import timing and traffic breakdown, one row per completed (or failed)
/// import. Keyed by the queue item's Guid, which the HistoryItem shares, so the
/// history page can look up the row directly. Lives in the metrics store so the
/// main database schema stays byte-compatible with upstream.
/// </summary>
public class ImportStat
{
    public Guid Id { get; set; }
    public string JobName { get; set; } = null!;
    public long CompletedAt { get; set; }

    // the "blue" progress phase: fetching first segments, par2s, archive headers
    public int DownloadMs { get; set; }

    // the "green" progress phase: on-add article-existence check; null when the
    // category doesn't run one
    public int? VerifyMs { get; set; }

    // whole wall time including database commit and post-processing
    public int TotalMs { get; set; }

    public bool Failed { get; set; }

    // JSON array: [{"Provider":"eweka","DownloadBytes":123,"VerifyBytes":45,"Articles":10}, ...]
    public string ProviderBytesJson { get; set; } = "[]";
}
