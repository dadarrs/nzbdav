using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Streams;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(
    List<MultiConnectionNntpClient> providers,
    Func<bool, bool>? useBackupProvidersForStatChecks = null,
    MetricsWriter? metricsWriter = null,
    ProviderBytesTracker? bytesTracker = null,
    ProviderUsageTracker? usageTracker = null
) : NntpClient
{
    /// <summary>
    /// Tags the current async flow with a read-session id so SegmentFetch metric rows can be
    /// attributed to the WebDAV read that caused them. Set around the GET handler's copy loop.
    /// </summary>
    private static readonly AsyncLocal<Guid?> ReadSessionScope = new();

    public static IDisposable BeginReadSessionScope(Guid readSessionId)
    {
        var previous = ReadSessionScope.Value;
        ReadSessionScope.Value = readSessionId;
        return new ScopeReleaser(() => ReadSessionScope.Value = previous);
    }

    private sealed class ScopeReleaser(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken ct)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.StatAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override async Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        CancellationToken cancellationToken
    )
    {
        if (segmentIds.Count == 0) return [];

        // Same semantics as running each STAT through RunFromPoolWithBackup individually: a segment
        // is only "missing" if it is missing on every provider. We send the batch to the first
        // provider, then re-query just the still-missing segments on each subsequent provider.
        var results = new UsenetStatResponse?[segmentIds.Count];
        var pending = new List<int>(segmentIds.Count);
        for (var i = 0; i < segmentIds.Count; i++) pending.Add(i);

        ExceptionDispatchInfo? lastException = null;
        var orderedProviders = GetOrderedProvidersForStatCheck(cancellationToken);
        foreach (var provider in orderedProviders)
        {
            if (pending.Count == 0) break;
            cancellationToken.ThrowIfCancellationRequested();

            if (lastException is not null)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug($"Encountered error during pipelined STAT batch: `{msg}`. Trying another provider.");
            }

            var subBatch = pending.Select(i => segmentIds[i]).ToList();
            IReadOnlyList<UsenetStatResponse> subResults;
            try
            {
                subResults = await provider.StatPipelinedAsync(subBatch, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                // The whole sub-batch is unresolved on this provider; leave those segments pending
                // so the next provider gets a chance at them.
                lastException = ExceptionDispatchInfo.Capture(e);
                continue;
            }

            RecordHealthCheckBytes(provider.Name, subBatch, subResults, cancellationToken);

            var stillPending = new List<int>();
            for (var k = 0; k < pending.Count; k++)
            {
                var idx = pending[k];
                var result = subResults[k];
                results[idx] = result;
                // Only keep re-querying segments this provider says are missing.
                if (result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                    stillPending.Add(idx);
            }

            pending = stillPending;
        }

        // Any segment that never received a result only failed because providers threw -- surface
        // that as an error rather than silently reporting the article as missing.
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is null)
            {
                if (lastException is not null) lastException.Throw();
                throw new Exception("There are no usenet providers configured.");
            }
        }

        return results!;
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.HeadAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedBodyAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedArticleAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DateAsync(cancellationToken), cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedBodyResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedArticleResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, Task<T>> task,
        CancellationToken cancellationToken
    ) where T : UsenetResponse
    {
        ExceptionDispatchInfo? lastException = null;
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses = null;
        var orderedProviders = GetOrderedProviders();
        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = orderedProviders[i];
            var isLastProvider = i == orderedProviders.Count - 1;

            if (lastException is not null)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug($"Encountered error during NNTP Operation: `{msg}`. Trying another provider.");
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await task.Invoke(provider).ConfigureAwait(false);

                // if no article with that message-id is found, try again with the next provider.
                if (result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                {
                    RecordFetch(provider.Name, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, i);
                    if (!isLastProvider)
                    {
                        (priorMisses ??= []).Add((provider.Name, SegmentFetch.FetchStatus.Missing));
                        continue;
                    }

                    return result;
                }

                RecordFetch(provider.Name, SegmentFetch.FetchStatus.Ok, stopwatch.ElapsedMilliseconds, i);
                usageTracker?.RecordSuccess(provider.Name);
                if (priorMisses is { Count: > 0 })
                {
                    // A later provider rescued a segment the earlier ones missed/failed.
                    usageTracker?.RecordFailoverSave();
                    RecordFailoverMisses(priorMisses, rescuer: provider.Name);
                }

                return WrapCounting(result, provider.Name);
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                var reason = ClassifyException(e);
                RecordFetch(provider.Name, reason, stopwatch.ElapsedMilliseconds, i);
                (priorMisses ??= []).Add((provider.Name, reason));
                lastException = ExceptionDispatchInfo.Capture(e);
            }
        }

        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    /// <summary>
    /// Wraps decoded body/article streams so every byte read is attributed to the provider that
    /// served it (feeds per-provider volume, data caps and throughput on the overview page).
    /// </summary>
    private T WrapCounting<T>(T result, string host) where T : UsenetResponse
    {
        if (bytesTracker == null) return result;
        return result switch
        {
            UsenetDecodedBodyResponse b =>
                (T)(object)(b with { Stream = new CountingYencStream(b.Stream, bytesTracker, host) }),
            UsenetDecodedArticleResponse a =>
                (T)(object)(a with { Stream = new CountingYencStream(a.Stream, bytesTracker, host) }),
            _ => result,
        };
    }

    private void RecordFetch(string host, SegmentFetch.FetchStatus status, long durationMs, int retries)
    {
        metricsWriter?.RecordFetch(new SegmentFetch
        {
            At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Provider = host,
            ReadSessionId = ReadSessionScope.Value,
            QueueItemId = ProviderUsageTracker.CurrentScopeId,
            Bytes = 0, // bytes flow lazily through CountingYencStream -> ProviderBytesTracker
            DurationMs = (int)Math.Min(durationMs, int.MaxValue),
            Status = status,
            Retries = retries,
        });
    }

    private void RecordFailoverMisses(
        List<(string Host, SegmentFetch.FetchStatus Reason)> priorMisses,
        string rescuer)
    {
        if (metricsWriter == null) return;
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var miss in priorMisses)
        {
            metricsWriter.RecordFailoverMiss(new FailoverMiss
            {
                At = at,
                FromProvider = miss.Host,
                ToProvider = rescuer,
                Reason = miss.Reason,
            });
        }
    }

    /// <summary>
    /// Accounts the NNTP protocol traffic of a STAT batch ("STAT &lt;id&gt;\r\n" commands plus
    /// one response line each) to the provider that served it. This is what byte-metered
    /// block accounts are actually billed for during health checks (~45 B per STAT), split
    /// by on-add vs background via the marker context on the cancellation token.
    /// </summary>
    private void RecordHealthCheckBytes(
        string host,
        List<string> subBatch,
        IReadOnlyList<UsenetStatResponse> subResults,
        CancellationToken ct)
    {
        if (bytesTracker == null) return;
        long bytes = 0;
        foreach (var id in subBatch) bytes += id.Length + 9; // "STAT <" + id + ">" + CRLF
        foreach (var result in subResults) bytes += result.ResponseMessage.Length + 2;
        var isBackground = ct.GetContext<BackgroundHealthCheckContext>() is not null;
        bytesTracker.AddHealthBytes(host, bytes, isBackground);
    }

    private static SegmentFetch.FetchStatus ClassifyException(Exception ex)
    {
        if (ex is TimeoutException) return SegmentFetch.FetchStatus.Timeout;
        if (ex is UnauthorizedAccessException) return SegmentFetch.FetchStatus.Auth;
        if (ex is IOException or System.Net.Sockets.SocketException) return SegmentFetch.FetchStatus.Network;
        return SegmentFetch.FetchStatus.Other;
    }

    private List<MultiConnectionNntpClient> GetOrderedProviders()
    {
        // Within a type tier: prefer the earliest-configured provider that has a free
        // connection right now, spilling to later ones only when it's saturated (OrderBy
        // is stable, so ties keep config-list order -- the user's drag-and-drop priority).
        // If everything is saturated, requests queue on the earliest-configured provider.
        var enabled = providers
            .Where(x => x.ProviderType != ProviderType.Disabled)
            .Where(x => !IsOverLimit(x))
            .OrderBy(x => x.ProviderType)
            .ThenByDescending(x => x.AvailableConnections > 0)
            .ToList();

        var healthy = enabled.Where(x => !x.IsTripped).ToList();

        // Always return at least one provider so cooldown probes can fire.
        return healthy.Count > 0 ? healthy : enabled;
    }

    private bool IsOverLimit(MultiConnectionNntpClient client)
    {
        var limit = client.ByteLimit;
        if (bytesTracker == null || !limit.HasValue || limit.Value <= 0) return false;
        var used = bytesTracker.GetLifetime(client.Name) + client.BytesUsedOffset;
        // Stop at the effective cutoff (95% of cap) so in-flight fetches that
        // already passed this check can't push the actual count past the cap.
        // See ProviderUsageHelper.EffectiveLimitFraction for the rationale.
        var effective = (long)(limit.Value * ProviderUsageHelper.EffectiveLimitFraction);
        return used >= effective;
    }

    /// <summary>
    /// Provider order for STAT health-check batches. Unlike downloads, STAT transfers no article
    /// bytes, so backup/block accounts can absorb existence checks nearly for free (protocol
    /// traffic may still be metered).
    /// When the "use backup providers for health checks" setting is on, every pooled provider
    /// plus any backup provider that opted in ("Backup &amp; Health Checks" type, or STAT
    /// pipelining enabled) is first-class here, ordered free-slot-first then by configured
    /// priority, so concurrent batches spill across them. Backup use may be scoped to on-add checks only:
    /// background checks are recognized by the marker context on their cancellation token.
    /// Non-eligible providers still follow, but only ever see the still-missing re-query.
    /// </summary>
    private List<MultiConnectionNntpClient> GetOrderedProvidersForStatCheck(CancellationToken ct)
    {
        var isBackgroundCheck = ct.GetContext<BackgroundHealthCheckContext>() is not null;
        var useBackupProviders = useBackupProvidersForStatChecks?.Invoke(isBackgroundCheck) ?? false;
        var enabled = providers
            .Where(x => x.ProviderType != ProviderType.Disabled)
            .OrderByDescending(x => x.ProviderType == ProviderType.Pooled
                                    || (useBackupProviders
                                        && (x.StatPipeliningEnabled
                                            || x.ProviderType == ProviderType.BackupAndStats)))
            // free-slot first, then stable config-list order (drag-and-drop priority)
            .ThenByDescending(x => x.AvailableConnections > 0)
            .ToList();

        var healthy = enabled.Where(x => !x.IsTripped).ToList();

        // Always return at least one provider so cooldown probes can fire.
        return healthy.Count > 0 ? healthy : enabled;
    }

    public override void Dispose()
    {
        foreach (var provider in providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}