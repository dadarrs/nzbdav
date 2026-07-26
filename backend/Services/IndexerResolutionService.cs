using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Backfills the true indexer name onto import-origin rows. Each NZB is recorded at
/// add-time with only the download-URL host (which, behind Prowlarr/NZBHydra, is just
/// the proxy). The real indexer is known to Radarr/Sonarr, which expose it on their
/// queue records keyed by downloadId — and that downloadId is the same Guid the import
/// carries. So this sweep periodically reads each arr's usenet queue, builds a
/// downloadId → indexer map, and stamps the matching unresolved origins.
///
/// Best-effort by nature: an arr keeps a record only while the download is in its queue,
/// so a small fraction of very fast imports can age out before a sweep sees them and
/// keep their URL-host fallback. Rows drop out of the sweep window on their own once
/// they're older than <see cref="LookbackWindow"/>, so the scan set stays bounded.
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

        // build downloadId -> indexer across every arr queue (best-effort per instance)
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
}
