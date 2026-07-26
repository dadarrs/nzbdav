using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Api.SabControllers.GetQueue;

public class GetQueueController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    private async Task<GetQueueResponse> GetQueueAsync(GetQueueRequest request)
    {
        // get in progress item
        var (inProgressQueueItem, progressPercentage) = queueManager.GetInProgressQueueItem();

        // get total count
        var ct = request.CancellationToken;
        var totalCount = await dbClient.GetQueueItemsCount(request.Category, ct).ConfigureAwait(false);

        // get queued items
        var getQueueItemsTask = dbClient.GetQueueItems(request.Category, request.Start, request.Limit, ct);
        var queueItems = (await getQueueItemsTask.ConfigureAwait(false))
            .Where(x => x.Id != inProgressQueueItem?.Id)
            .ToArray();

        // items that will actually become slots (in-progress item prepended for page 1)
        var slotItems = queueItems
            .Prepend(request is { Start: 0, Limit: > 0 } ? inProgressQueueItem : null)
            .Where(queueItem => queueItem != null)
            .ToList();

        // join in the indexer origin (metrics store; keyed by the shared item id)
        var indexerByItemId = await GetIndexersByItemIdAsync(
            slotItems.Select(x => x!.Id), ct).ConfigureAwait(false);

        // get slots
        var slots = slotItems
            .Select((queueItem, index) =>
            {
                var percentage = (queueItem == inProgressQueueItem ? progressPercentage : 0)!.Value;
                var status = queueItem == inProgressQueueItem ? "Downloading" : "Queued";
                var slot = GetQueueResponse.QueueSlot.FromQueueItem(queueItem!, index, percentage, status);
                slot.Indexer = indexerByItemId.GetValueOrDefault(queueItem!.Id);
                return slot;
            })
            .ToList();

        // return response
        return new GetQueueResponse()
        {
            Queue = new GetQueueResponse.QueueObject()
            {
                Paused = false,
                Slots = slots,
                TotalCount = totalCount,
            }
        };
    }

    /// <summary>
    /// Maps item ids to their effective indexer name (resolved arr indexer, else the
    /// download-URL host) from the metrics store. Items with no origin are absent.
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
        var request = new GetQueueRequest(httpContext);
        return Ok(await GetQueueAsync(request).ConfigureAwait(false));
    }
}