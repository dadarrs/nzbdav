using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Backfills the true indexer name onto import-origin rows. Each NZB is recorded at
/// add-time with only the download-URL host (which, behind Prowlarr/NZBHydra, is just
/// the proxy — and for arr grabs, added via addfile, there's no URL at all). The real
/// indexer is known to Radarr/Sonarr, keyed by downloadId — the same Guid the import
/// carries. So this sweep reads each arr and builds a downloadId → indexer map, then
/// stamps the matching unresolved origins.
///
/// The map is built from two sources: the live queue AND grabbed history. History is
/// the source that actually works — nzbdav verifies+mounts in seconds, so an import
/// leaves the arr queue almost immediately, but its grabbed-history event is written at
/// grab time and persists. A queue-only sweep only ever catches the rare import still
/// sitting in the queue during a tick; history catches all of them.
///
/// Rows drop out of the sweep window on their own once they're older than
/// <see cref="LookbackWindow"/>, so the scan set (and the history paging) stays bounded.
/// </summary>
public class IndexerResolutionService(ConfigManager configManager) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LookbackWindow = TimeSpan.FromHours(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            try
            {
                await ResolveOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Debug($"Indexer resolution sweep failed: {e.Message}");
            }
        }
    }

    private async Task ResolveOnceAsync(CancellationToken ct)
    {
        var arrConfig = configManager.GetArrConfig();
        if (arrConfig.GetInstanceCount() == 0) return;

        var cutoff = DateTimeOffset.UtcNow.Subtract(LookbackWindow).ToUnixTimeMilliseconds();

        await using var metrics = new MetricsDbContext();
        var pending = await metrics.ImportOrigins
            .Where(x => !x.Resolved && x.CreatedAt >= cutoff)
            .ToListAsync(ct).ConfigureAwait(false);
        if (pending.Count == 0) return;

        // build downloadId -> indexer across every arr, from the live queue AND grabbed
        // history (best-effort per instance). History is the reliable source; the queue
        // read is a cheap fast-path for imports still in flight.
        var indexerByDownloadId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var client in arrConfig.GetArrClients())
        {
            try
            {
                var queue = await client.GetQueueAsync().ConfigureAwait(false);
                foreach (var record in queue.Records)
                {
                    if (string.IsNullOrEmpty(record.DownloadId)) continue;
                    if (string.IsNullOrWhiteSpace(record.Indexer)) continue;
                    indexerByDownloadId.TryAdd(record.DownloadId, record.Indexer.Trim());
                }
            }
            catch (Exception e)
            {
                Log.Debug($"Indexer resolution: could not read queue from `{client.Host}`: {e.Message}");
            }

            try
            {
                await AddGrabbedHistoryAsync(client, indexerByDownloadId, cutoff, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Debug($"Indexer resolution: could not read history from `{client.Host}`: {e.Message}");
            }
        }

        if (indexerByDownloadId.Count == 0) return;

        var changed = 0;
        foreach (var origin in pending)
        {
            if (!indexerByDownloadId.TryGetValue(origin.Id.ToString(), out var indexer)) continue;
            origin.ArrIndexer = StringUtil.TruncateToLength(indexer, 255);
            origin.Resolved = true;
            changed++;
        }

        if (changed > 0)
        {
            await metrics.SaveChangesAsync(ct).ConfigureAwait(false);
            Log.Debug($"Indexer resolution: stamped {changed} import(s) with their arr indexer.");
        }
    }

    /// <summary>
    /// Pages back through an arr's grabbed history (newest first) folding downloadId →
    /// indexer into <paramref name="map"/>, and stops as soon as it reaches records older
    /// than the lookback cutoff (history is date-descending) or a hard page cap. Bounded so
    /// a busy arr can never make a sweep unbounded.
    /// </summary>
    private static async Task AddGrabbedHistoryAsync(
        ArrClient client, IDictionary<string, string> map, long cutoff, CancellationToken ct)
    {
        const int pageSize = 200;
        const int maxPages = 10;
        for (var page = 1; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var history = await client.GetGrabbedHistoryAsync(page, pageSize).ConfigureAwait(false);
            if (history.Records.Count == 0) break;

            var reachedWindowEnd = false;
            foreach (var record in history.Records)
            {
                if (record.Date.ToUnixTimeMilliseconds() < cutoff)
                {
                    reachedWindowEnd = true;
                    continue;
                }

                if (string.IsNullOrEmpty(record.DownloadId)) continue;
                var indexer = record.EffectiveIndexer;
                if (string.IsNullOrWhiteSpace(indexer)) continue;
                map.TryAdd(record.DownloadId, indexer.Trim());
            }

            // records are date-descending: once we've crossed the cutoff, or this page
            // wasn't full, nothing older is worth fetching.
            if (reachedWindowEnd || history.Records.Count < pageSize) break;
        }
    }
}
