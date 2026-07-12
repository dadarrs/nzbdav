using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckHistory;

public class GetHealthCheckHistoryResponse : BaseApiResponse
{
    public required List<HealthCheckStat> Stats { get; init; }
    public required List<HealthCheckResult> Items { get; init; }

    // deep links for repaired rows, keyed by HealthCheckResult id; kept out of
    // Items because HealthCheckResult is a main-db entity shared with upstream
    public Dictionary<Guid, ArrLink> ArrLinks { get; init; } = new();

    // repaired/deleted counts per lookback window (7/30/365 days), so the stat
    // cards can offer one clickable chip per window
    public List<RepairWindowStat> RepairWindowStats { get; init; } = [];

    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }

    public class ArrLink
    {
        public required string Url { get; init; }
        public string? Title { get; init; }
        public required string Kind { get; init; }
    }

    public class RepairWindowStat
    {
        public required int WindowDays { get; init; }
        public required int Repaired { get; init; }
        public required int Deleted { get; init; }
    }
}