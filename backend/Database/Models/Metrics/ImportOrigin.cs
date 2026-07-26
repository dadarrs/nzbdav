namespace NzbWebDAV.Database.Models.Metrics;

/// <summary>
/// Where an import came from — the indexer that supplied the NZB. Keyed by the
/// queue item's Guid, which the HistoryItem and ImportStat share, so the history
/// page and the overview can join it in directly. Written at add-time with the
/// download-URL host as an immediate fallback, then enriched with the true indexer
/// name resolved from the Radarr/Sonarr queue by <c>IndexerResolutionService</c>.
/// Lives in the metrics store so the main database schema stays byte-compatible
/// with upstream.
/// </summary>
public class ImportOrigin
{
    public Guid Id { get; set; }

    // Host of the download URL captured when the NZB was added (e.g. "prowlarr:9696"
    // or "api.nzbgeek.info"). Null for direct file uploads. We deliberately store only
    // the host, never the full URL — download URLs carry the indexer apikey.
    public string? UrlHost { get; set; }

    // True indexer name resolved from the Radarr/Sonarr queue (e.g. "DrunkenSlug
    // (Prowlarr)"). Null until resolved, or when no arr match was ever found.
    public string? ArrIndexer { get; set; }

    // Set once resolution has succeeded, so the backfill sweep stops reconsidering the
    // row. Unmatched rows stay false but age out of the sweep window on their own.
    public bool Resolved { get; set; }

    public long CreatedAt { get; set; }

    // The name to group and display by, preferring the resolved indexer over the raw
    // host. Not mapped — computed from the two stored columns.
    public string EffectiveName =>
        !string.IsNullOrWhiteSpace(ArrIndexer) ? ArrIndexer!
        : !string.IsNullOrWhiteSpace(UrlHost) ? UrlHost!
        : "Unknown";
}
