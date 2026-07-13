namespace NzbWebDAV.Api.Controllers.GetImportStats;

public class GetImportStatsResponse : BaseApiResponse
{
    public bool Found { get; init; }
    public string? JobName { get; init; }
    public long CompletedAt { get; init; }
    public int DownloadMs { get; init; }
    public int? VerifyMs { get; init; }
    public int TotalMs { get; init; }
    public bool Failed { get; init; }
    public List<ProviderStat> Providers { get; init; } = [];

    public class ProviderStat
    {
        public string Provider { get; set; } = "";
        public long DownloadBytes { get; set; }
        public long VerifyBytes { get; set; }
        public int Articles { get; set; }
    }
}
