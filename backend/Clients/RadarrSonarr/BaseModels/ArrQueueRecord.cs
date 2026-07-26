using System.Text.Json.Serialization;

namespace NzbWebDAV.Clients.RadarrSonarr.BaseModels;

public class ArrQueueRecord
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("downloadClient")]
    public string? DownloadClient { get; set; }

    [JsonPropertyName("indexer")]
    public string? Indexer { get; set; }

    // The download-client id the arr assigned this grab — for a SABnzbd client this is
    // the nzo_id we returned from addfile/addurl, i.e. our queue item's Guid as a string.
    // Used to match an arr queue record back to the import that produced it.
    [JsonPropertyName("downloadId")]
    public string? DownloadId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("trackedDownloadStatus")]
    public string? TrackedDownloadStatus { get; set; }

    [JsonPropertyName("trackedDownloadState")]
    public string? TrackedDownloadState { get; set; }

    [JsonPropertyName("statusMessages")]
    public List<ArrQueueStatusMessage> StatusMessages { get; set; } = [];

    public bool HasStatusMessage(string message)
    {
        return StatusMessages
            .SelectMany(x => x.Messages)
            .Any(x => x.Contains(message));
    }
}