using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckHistory;

[ApiController]
[Route("api/get-health-check-history")]
public class GetHealthCheckHistoryController(DavDatabaseClient dbClient, ConfigManager configManager)
    : BaseApiController
{
    private async Task<GetHealthCheckHistoryResponse> GetHealthCheckHistory(GetHealthCheckHistoryRequest request)
    {
        var now = DateTime.UtcNow;
        var tomorrow = now.AddDays(1);
        var thirtyDaysAgo = now.AddDays(-30);
        var statsPromise = dbClient.GetHealthCheckStatsAsync(thirtyDaysAgo, tomorrow);

        var query = dbClient.Ctx.HealthCheckResults.AsQueryable();
        if (request.Search is not null)
        {
            foreach (var token in request.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = token.ToLower();
                query = query.Where(x => x.Path.ToLower().Contains(t));
            }
        }

        if (request.RepairStatus is { } repairStatus)
            query = query.Where(x => x.RepairStatus == (HealthCheckResult.RepairAction)repairStatus);

        if (request.WindowDays is { } windowDays)
        {
            var cutoff = now.AddDays(-windowDays);
            query = query.Where(x => x.CreatedAt >= cutoff);
        }

        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync().ConfigureAwait(false);

        return new GetHealthCheckHistoryResponse()
        {
            Stats = await statsPromise.ConfigureAwait(false),
            Items = items,
            ArrLinks = await BuildArrLinksAsync(items).ConfigureAwait(false),
            RepairWindowStats = await BuildRepairWindowStatsAsync().ConfigureAwait(false),
            TotalCount = totalCount,
            Page = request.Page,
            PageSize = request.PageSize,
        };
    }

    /// <summary>
    /// Repaired/deleted counts for the 7/30/365-day lookback windows. One query for
    /// the widest window, bucketed in memory -- repair actions are rare events, so
    /// a year of them stays small.
    /// </summary>
    private async Task<List<GetHealthCheckHistoryResponse.RepairWindowStat>> BuildRepairWindowStatsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var widestCutoff = now.AddDays(-365);
        var rows = await dbClient.Ctx.HealthCheckResults
            .Where(x => x.CreatedAt >= widestCutoff &&
                        (x.RepairStatus == HealthCheckResult.RepairAction.Repaired ||
                         x.RepairStatus == HealthCheckResult.RepairAction.Deleted))
            .Select(x => new { x.CreatedAt, x.RepairStatus })
            .ToListAsync().ConfigureAwait(false);

        return new[] { 7, 30, 365 }
            .Select(windowDays =>
            {
                var cutoff = now.AddDays(-windowDays);
                return new GetHealthCheckHistoryResponse.RepairWindowStat
                {
                    WindowDays = windowDays,
                    Repaired = rows.Count(r => r.CreatedAt >= cutoff &&
                        r.RepairStatus == HealthCheckResult.RepairAction.Repaired),
                    Deleted = rows.Count(r => r.CreatedAt >= cutoff &&
                        r.RepairStatus == HealthCheckResult.RepairAction.Deleted),
                };
            })
            .ToList();
    }

    /// <summary>
    /// Deep links for repaired and deleted rows, keyed by HealthCheckResult id. The arr
    /// item was captured in a RepairEvent when the repair/deletion happened (a file-path
    /// lookup can no longer resolve it afterwards). The link base prefers the instance's
    /// configured PublicUrl so links work from outside the network.
    /// </summary>
    private async Task<Dictionary<Guid, GetHealthCheckHistoryResponse.ArrLink>> BuildArrLinksAsync(
        List<HealthCheckResult> items)
    {
        var links = new Dictionary<Guid, GetHealthCheckHistoryResponse.ArrLink>();
        var repairedItems = items
            .Where(x => x.RepairStatus is HealthCheckResult.RepairAction.Repaired
                or HealthCheckResult.RepairAction.Deleted)
            .ToList();
        if (repairedItems.Count == 0) return links;

        var davItemIds = repairedItems.Select(x => x.DavItemId).Distinct().ToList();
        await using var metrics = new MetricsDbContext();
        var latestEventByDavItem = (await metrics.RepairEvents
                .Where(e => davItemIds.Contains(e.DavItemId))
                .OrderBy(e => e.At)
                .ToListAsync().ConfigureAwait(false))
            .GroupBy(e => e.DavItemId)
            .ToDictionary(g => g.Key, g => g.Last());

        var arrConfig = configManager.GetArrConfig();
        foreach (var item in repairedItems)
        {
            if (!latestEventByDavItem.TryGetValue(item.DavItemId, out var repairEvent)) continue;

            var instances = repairEvent.ArrKind == "radarr"
                ? arrConfig.RadarrInstances
                : arrConfig.SonarrInstances;
            var configured = instances.FirstOrDefault(x =>
                string.Equals(x.Host, repairEvent.ArrHost, StringComparison.OrdinalIgnoreCase));
            var baseUrl = (string.IsNullOrWhiteSpace(configured?.PublicUrl)
                ? repairEvent.ArrHost
                : configured!.PublicUrl!).TrimEnd('/');

            var url = repairEvent.ArrTitleSlug == null
                ? baseUrl
                : repairEvent.ArrKind == "radarr"
                    ? $"{baseUrl}/movie/{repairEvent.ArrTitleSlug}"
                    : $"{baseUrl}/series/{repairEvent.ArrTitleSlug}";

            links[item.Id] = new GetHealthCheckHistoryResponse.ArrLink
            {
                Url = url,
                Title = repairEvent.ArrTitle,
                Kind = repairEvent.ArrKind,
            };
        }

        return links;
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetHealthCheckHistoryRequest(HttpContext);
        var response = await GetHealthCheckHistory(request).ConfigureAwait(false);
        return Ok(response);
    }
}
