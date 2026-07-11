namespace NzbWebDAV.Database.Models.Metrics;

/// <summary>
/// Records which arr media-item a repair re-searched or a deletion belonged to,
/// captured when the repair/deletion happens because a file-path lookup can no
/// longer resolve the item afterwards. Lives in the metrics store so the main
/// database schema stays byte-compatible with upstream. Joined to
/// HealthCheckResults by DavItemId to deep-link rows on the health page.
/// </summary>
public class RepairEvent
{
    public Guid Id { get; set; }
    public long At { get; set; }
    public Guid DavItemId { get; set; }
    public string Path { get; set; } = null!;

    // "radarr" | "sonarr"
    public string ArrKind { get; set; } = null!;

    // the instance's configured (internal) host at repair time; the public URL,
    // if any, is resolved from config at read time so later edits take effect
    public string ArrHost { get; set; } = null!;

    // radarr movie id / sonarr series id
    public int ArrItemId { get; set; }
    public string? ArrTitleSlug { get; set; }
    public string? ArrTitle { get; set; }
}
