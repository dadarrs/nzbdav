using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.SabControllers.GetHistory;

public class GetHistoryController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    private async Task<GetHistoryResponse> GetHistoryAsync(GetHistoryRequest request)
    {
        // get query
        IQueryable<HistoryItem> query = dbClient.Ctx.HistoryItems;
        if (request.NzoIds.Count > 0)
            query = query.Where(q => request.NzoIds.Contains(q.Id));
        if (request.Category != null)
            query = query.Where(q => q.Category == request.Category);

        // get total count
        var totalCountPromise = query
            .CountAsync(request.CancellationToken);

        // get history items
        var historyItemsPromise = query
            .OrderByDescending(q => q.CreatedAt)
            .Skip(request.Start)
            .Take(request.Limit)
            .ToArrayAsync(request.CancellationToken);

        // await results
        var totalCount = await totalCountPromise.ConfigureAwait(false);
        var historyItems = await historyItemsPromise.ConfigureAwait(false);

        // get download folders
        var downloadFolderIds = historyItems.Select(x => x.DownloadDirId).ToHashSet();
        var davItems = await dbClient.Ctx.Items
            .Where(x => downloadFolderIds.Contains(x.Id))
            .ToArrayAsync(request.CancellationToken).ConfigureAwait(false);
        var davItemsDict = davItems
            .ToDictionary(x => x.Id, x => x);

        // join in the indexer origin (metrics store; keyed by the shared item id)
        var indexerByItemId = await GetIndexersByItemIdAsync(
            historyItems.Select(x => x.Id), request.CancellationToken).ConfigureAwait(false);

        // get slots
        var slots = historyItems
            .Select(x =>
            {
                var slot = GetHistoryResponse.HistorySlot.FromHistoryItem(
                    x,
                    x.DownloadDirId != null ? davItemsDict.GetValueOrDefault(x.DownloadDirId.Value) : null,
                    configManager
                );
                slot.Indexer = indexerByItemId.GetValueOrDefault(x.Id);
                return slot;
            })
            .ToList();

        // return response
        return new GetHistoryResponse()
        {
            History = new GetHistoryResponse.HistoryObject()
            {
                Slots = slots,
                TotalCount = totalCount,
            }
        };
    }

    /// <summary>
    /// Maps item ids to their effective indexer name (resolved arr indexer, else the
    /// download-URL host) from the metrics store. Items with no origin, or an origin
    /// carrying neither, are simply absent — the slot then has no indexer badge.
    /// </summary>
    private static async Task<Dictionary<Guid, string>> GetIndexersByItemIdAsync(
        IEnumerable<Guid> itemIds, CancellationToken ct)
    {
        var ids = itemIds.ToHashSet();
        if (ids.Count == 0) return new Dictionary<Guid, string>();

        await using var metrics = new MetricsDbContext();
        var origins = await metrics.ImportOrigins
            .Where(o => ids.Contains(o.Id))
            .Select(o => new { o.Id, o.ArrIndexer, o.UrlHost })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new Dictionary<Guid, string>();
        foreach (var o in origins)
        {
            var name = !string.IsNullOrWhiteSpace(o.ArrIndexer) ? o.ArrIndexer
                : !string.IsNullOrWhiteSpace(o.UrlHost) ? o.UrlHost
                : null;
            if (name != null) result[o.Id] = name;
        }

        return result;
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetHistoryRequest(httpContext, configManager);
        return Ok(await GetHistoryAsync(request).ConfigureAwait(false));
    }
}