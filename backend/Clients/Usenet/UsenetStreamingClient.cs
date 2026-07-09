using System.Diagnostics;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Websocket;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class UsenetStreamingClient : WrappingNntpClient
{
    private readonly ConfigManager _configManager;

    public UsenetStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        MetricsWriter? metricsWriter = null,
        ProviderBytesTracker? bytesTracker = null,
        ProviderUsageTracker? usageTracker = null)
        : base(CreateDownloadingNntpClient(configManager, websocketManager, metricsWriter, bytesTracker, usageTracker))
    {
        _configManager = configManager;

        // when config changes, create a new MultiProviderClient to use instead.
        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // if unrelated config changed, do nothing
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;

            // update the connection-pool according to the new config
            var newUsenetClient = CreateDownloadingNntpClient(
                configManager, websocketManager, metricsWriter, bytesTracker, usageTracker);
            ReplaceUnderlyingClient(newUsenetClient);
        };
    }

    /// <summary>
    /// STAT-checks every segment, failing fast on the first one that is missing on all providers.
    /// When the NNTP pipelining master switch is on, segments are checked in pipelined batches
    /// (per-provider pipelining is gated further down the chain); otherwise this defers to the
    /// base one-command-per-round-trip implementation.
    /// </summary>
    public override async Task CheckAllSegmentsAsync
    (
        IEnumerable<string> segmentIds,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        // Stay on the original one-at-a-time path unless pipelining is switched on AND at least one
        // health-check-eligible provider has actually opted in -- so enabling the master switch
        // alone changes nothing until a provider is tested and enabled.
        var ids = segmentIds as IReadOnlyCollection<string> ?? segmentIds.ToList();
        var depth = _configManager.GetNntpPipeliningDepth();
        var useBackupProviders = _configManager.UseBackupProvidersForHealthChecks();
        var anyProviderPipelined = _configManager.GetUsenetProviderConfig()
            .Providers.Any(p => p.StatPipeliningEnabled && p.IsStatCheckEligible(useBackupProviders));
        var stopwatch = Stopwatch.StartNew();
        if (!_configManager.GetNntpPipeliningEnabled() || depth <= 1 || !anyProviderPipelined)
        {
            Log.Information(
                "Article health check: LINEAR STAT path ({Count} segments, concurrency {Concurrency})",
                ids.Count, concurrency);
            await base.CheckAllSegmentsAsync(ids, concurrency, progress, cancellationToken)
                .ConfigureAwait(false);
            LogCompleted("LINEAR", ids.Count, stopwatch);
            return;
        }

        Log.Information(
            "Article health check: PIPELINED STAT path ({Count} segments, depth {Depth}, concurrency {Concurrency})",
            ids.Count, depth, concurrency);

        // Contextual so the background-health-check marker survives onto the batch tokens.
        using var childCt = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = childCt.Token;

        // Check the segments in pipelined batches of `depth`, running up to `concurrency` batches at
        // once so every pooled connection stays busy. Depth is the user-facing lever: a deeper
        // pipeline hides more round-trip latency; if a provider handles deep batches poorly, lower it.
        var batches = ids.Chunk(depth);
        var tasks = batches
            .Select(async batch => (
                Batch: batch,
                Results: await StatPipelinedAsync(batch, token).ConfigureAwait(false)
            ))
            .WithConcurrencyAsync(concurrency);

        var processed = 0;
        await foreach (var task in tasks.ConfigureAwait(false))
        {
            for (var i = 0; i < task.Batch.Length; i++)
            {
                progress?.Report(++processed);
                if (task.Results[i].ResponseType == UsenetResponseType.ArticleExists) continue;
                await childCt.CancelAsync().ConfigureAwait(false);
                throw new UsenetArticleNotFoundException(task.Batch[i]);
            }
        }

        LogCompleted("PIPELINED", ids.Count, stopwatch);
    }

    private static void LogCompleted(string path, int count, Stopwatch stopwatch)
    {
        var elapsed = stopwatch.Elapsed.TotalSeconds;
        var rate = elapsed > 0 ? (int)(count / elapsed) : count;
        Log.Information(
            "Article health check: {Path} path completed ({Count} segments in {Elapsed:F1}s, {Rate} stat/s)",
            path, count, elapsed, rate);
    }

    private static DownloadingNntpClient CreateDownloadingNntpClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        MetricsWriter? metricsWriter = null,
        ProviderBytesTracker? bytesTracker = null,
        ProviderUsageTracker? usageTracker = null
    )
    {
        var multiProviderClient = CreateMultiProviderClient(
            configManager, websocketManager, metricsWriter, bytesTracker, usageTracker);
        return new DownloadingNntpClient(multiProviderClient, configManager);
    }

    private static MultiProviderNntpClient CreateMultiProviderClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        MetricsWriter? metricsWriter = null,
        ProviderBytesTracker? bytesTracker = null,
        ProviderUsageTracker? usageTracker = null
    )
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        var connectionPoolStats = new ConnectionPoolStats(
            providerConfig,
            configManager.UseBackupProvidersForHealthChecks,
            websocketManager);
        var providerClients = providerConfig.Providers
            .Select((provider, index) => CreateProviderClient(
                provider,
                connectionPoolStats.GetOnConnectionPoolChanged(index)
            ))
            .ToList();
        // The flags are read through a delegate so toggling the settings takes effect
        // immediately; the client itself is only rebuilt when the provider list changes.
        // Backup providers may be scoped to on-add checks only.
        return new MultiProviderNntpClient(
            providerClients,
            isBackgroundCheck => configManager.UseBackupProvidersForHealthChecks()
                                 && (!isBackgroundCheck ||
                                     configManager.UseBackupProvidersForBackgroundHealthChecks()),
            metricsWriter,
            bytesTracker,
            usageTracker);
    }

    private static MultiConnectionNntpClient CreateProviderClient
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged
    )
    {
        var connectionPool = CreateNewConnectionPool(
            maxConnections: connectionDetails.MaxConnections,
            connectionFactory: ct => CreateNewConnection(connectionDetails, ct),
            onConnectionPoolChanged
        );
        var circuitBreaker = new ProviderCircuitBreaker(connectionDetails.Host);
        return new MultiConnectionNntpClient(
            connectionPool,
            connectionDetails.Type,
            circuitBreaker,
            connectionDetails.Host,
            connectionDetails.StatPipeliningEnabled);
    }

    private static ConnectionPool<INntpClient> CreateNewConnectionPool
    (
        int maxConnections,
        Func<CancellationToken, ValueTask<INntpClient>> connectionFactory,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged
    )
    {
        var connectionPool = new ConnectionPool<INntpClient>(maxConnections, connectionFactory);
        connectionPool.OnConnectionPoolChanged += onConnectionPoolChanged;
        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections);
        onConnectionPoolChanged(connectionPool, args);
        return connectionPool;
    }

    public static async ValueTask<INntpClient> CreateNewConnection
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        CancellationToken ct
    )
    {
        var connection = new BaseNntpClient();
        var host = connectionDetails.Host;
        var port = connectionDetails.Port;
        var useSsl = connectionDetails.UseSsl;
        var user = connectionDetails.User;
        var pass = connectionDetails.Pass;
        await connection.ConnectAsync(host, port, useSsl, ct).ConfigureAwait(false);
        await connection.AuthenticateAsync(user, pass, ct).ConfigureAwait(false);
        return connection;
    }
}