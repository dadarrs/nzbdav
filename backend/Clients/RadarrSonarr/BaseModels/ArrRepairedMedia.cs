namespace NzbWebDAV.Clients.RadarrSonarr.BaseModels;

/// <summary>
/// Identifies the media item an arr instance re-searched during a repair. Captured
/// at repair time because the file is deleted from the arr as part of the repair,
/// so the item can no longer be resolved from the path afterwards. Kind and slug
/// feed the deep link shown on the health page (radarr: /movie/{titleSlug},
/// sonarr: /series/{titleSlug}).
/// </summary>
public class ArrRepairedMedia
{
    public const string RadarrKind = "radarr";
    public const string SonarrKind = "sonarr";

    public required string Kind { get; init; }
    public required int ItemId { get; init; }
    public string? TitleSlug { get; init; }
    public string? Title { get; init; }
}
