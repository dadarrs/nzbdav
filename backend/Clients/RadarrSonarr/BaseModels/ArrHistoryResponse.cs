using System.Text.Json.Serialization;

namespace NzbWebDAV.Clients.RadarrSonarr.BaseModels;

/// <summary>A page of Radarr/Sonarr history records.</summary>
public class ArrHistoryResponse
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalRecords")]
    public int TotalRecords { get; set; }

    [JsonPropertyName("records")]
    public List<ArrHistoryRecord> Records { get; set; } = [];
}
