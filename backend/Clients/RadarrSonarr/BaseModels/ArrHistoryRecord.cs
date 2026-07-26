using System.Text.Json.Serialization;

namespace NzbWebDAV.Clients.RadarrSonarr.BaseModels;

/// <summary>
/// A Radarr/Sonarr history record. We only ever read "grabbed" events (eventType=1),
/// which carry the download-client id and the indexer that supplied the release. This
/// is the reliable source for indexer resolution: a grabbed event is written at grab
/// time and persists in history, whereas the matching queue record vanishes as soon as
/// the (fast) nzbdav import completes — so a queue-only sweep misses almost everything.
/// </summary>
public class ArrHistoryRecord
{
    // The download-client id the arr assigned this grab — for a SABnzbd client this is
    // the nzo_id we returned from addfile/addurl, i.e. our queue item's Guid as a string.
    [JsonPropertyName("downloadId")]
    public string? DownloadId { get; set; }

    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }

    // Some arr versions surface the indexer at the top level of a grabbed record...
    [JsonPropertyName("indexer")]
    public string? Indexer { get; set; }

    // ...others only inside the event's data bag. Prefer the top-level, fall back to data.
    [JsonPropertyName("data")]
    public ArrHistoryData? Data { get; set; }

    public string? EffectiveIndexer =>
        !string.IsNullOrWhiteSpace(Indexer) ? Indexer : Data?.Indexer;

    public class ArrHistoryData
    {
        [JsonPropertyName("indexer")]
        public string? Indexer { get; set; }
    }
}
