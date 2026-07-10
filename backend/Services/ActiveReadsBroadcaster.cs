using System.Text.Json;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Ticks once per second to publish the current set of active WebDAV read
/// sessions plus their per-backbone segment counts over the websocket. When no
/// sessions are active, the loop is mostly idle (just a sleep + a Count check).
/// Sends nothing when nothing has changed since the last broadcast.
/// </summary>
public class ActiveReadsBroadcaster(
    ActiveReadRegistry registry,
    ProviderUsageTracker usageTracker,
    WebsocketManager websocketManager,
    MetricsWriter metricsWriter,
    ConfigManager configManager
) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private string? _lastPayload;
    private bool _wasEmpty = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                await BroadcastTickAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception e)
            {
                Log.Debug(e, "ActiveReadsBroadcaster tick failed");
            }
        }
    }

    private async Task BroadcastTickAsync()
    {
        // Prune sessions that haven't been touched in the activity window first,
        // so their counters don't leak in the tracker. Each pruned entry becomes
        // a terminal ReadSession row so the dashboard can show historical reads.
        var pruned = registry.PruneExpired();
        foreach (var entry in pruned)
        {
            var failoverSaves = usageTracker.GetFailoverSaves(entry.Id);
            usageTracker.Clear(entry.Id);

            // Library scans, ffprobe opens and instantly-aborted requests read nothing --
            // don't clutter stream history with 0-byte rows.
            var bytesServed = Interlocked.Read(ref entry.BytesRead);
            if (bytesServed == 0) continue;

            // Players split one viewing into many sessions (any pause/seek gap longer than
            // the 15s activity window ends one). Fold this session into a recent row for
            // the same path so history reads as one entry per viewing, not dozens.
            if (await TryMergeRecentSessionAsync(entry, bytesServed, failoverSaves).ConfigureAwait(false))
                continue;

            metricsWriter.RecordSession(new ReadSession
            {
                Id = entry.Id,
                StartedAt = entry.StartedAt.ToUnixTimeMilliseconds(),
                EndedAt = entry.LastActivityAt.ToUnixTimeMilliseconds(),
                DurationMs = (int)Math.Min(int.MaxValue,
                    (entry.LastActivityAt - entry.StartedAt).TotalMilliseconds),
                Path = entry.Path,
                FileSize = entry.FileSize,
                BytesServed = bytesServed,
                BytesFetched = 0, // not measured per-session yet (bytes stream after fetch attribution)
                FailoverSaves = (int)Math.Min(int.MaxValue, failoverSaves),
                ClientUserAgent = null,
                ClientIp = null,
                EndReason = ReadSession.EndReasonCode.Completed,
            });
        }

        var entries = registry.Snapshot();

        // Common case: nothing active, nothing was active. Skip serialization entirely.
        if (entries.Count == 0 && _wasEmpty) return;

        var usage = usageTracker.SnapshotMany(entries.Select(e => e.Id));
        var nicknamesByHost = configManager.GetUsenetProviderConfig().Providers
            .Where(p => !string.IsNullOrWhiteSpace(p.Nickname))
            .GroupBy(p => p.Host, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Nickname, StringComparer.OrdinalIgnoreCase);
        var snapshot = new
        {
            reads = entries.Select(e => new
            {
                id = e.Id,
                fileName = e.FileName,
                path = e.Path,
                startedAt = e.StartedAt.ToUnixTimeMilliseconds(),
                lastActivityAt = e.LastActivityAt.ToUnixTimeMilliseconds(),
                bytesRead = Interlocked.Read(ref e.BytesRead),
                currentOffset = Interlocked.Read(ref e.CurrentOffset),
                fileSize = e.FileSize,
                providers = (usage.GetValueOrDefault(e.Id) ?? new Dictionary<string, long>())
                    .Select(kv => new
                    {
                        host = kv.Key,
                        nickname = nicknamesByHost.GetValueOrDefault(kv.Key),
                        segments = kv.Value,
                    })
                    .OrderByDescending(p => p.segments)
                    .ToList()
            }).ToList()
        };

        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (payload == _lastPayload) return;
        _lastPayload = payload;
        _wasEmpty = entries.Count == 0;
        await websocketManager.SendMessage(WebsocketTopic.ActiveReads, payload).ConfigureAwait(false);
    }

    // Merge window: a resumed playback within this gap continues the same history row.
    private static readonly TimeSpan MergeWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Folds a pruned session into the most recent persisted row for the same path when the
    /// gap between them is small. Merges by path only (client identity isn't persisted), so
    /// two clients watching the same file within the window collapse into one row -- an
    /// acceptable trade for keeping one row per viewing. Returns false on any error so the
    /// caller falls back to a plain insert; metrics must never break the broadcaster.
    /// </summary>
    private async Task<bool> TryMergeRecentSessionAsync(
        ActiveReadRegistry.Entry entry, long bytesServed, long failoverSaves)
    {
        try
        {
            var cutoff = entry.StartedAt.Subtract(MergeWindow).ToUnixTimeMilliseconds();
            await using var db = new Database.MetricsDbContext();
            var recent = db.ReadSessions
                .Where(x => x.Path == entry.Path && x.EndedAt >= cutoff)
                .OrderByDescending(x => x.EndedAt)
                .FirstOrDefault();
            if (recent == null) return false;

            recent.EndedAt = entry.LastActivityAt.ToUnixTimeMilliseconds();
            recent.DurationMs = (int)Math.Min(int.MaxValue,
                recent.DurationMs + (entry.LastActivityAt - entry.StartedAt).TotalMilliseconds);
            recent.BytesServed += bytesServed;
            recent.FailoverSaves = (int)Math.Min(int.MaxValue, recent.FailoverSaves + failoverSaves);
            if (entry.FileSize is { } size) recent.FileSize = size;
            await db.SaveChangesAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Failed to merge read session; inserting a new row instead");
            return false;
        }
    }
}
