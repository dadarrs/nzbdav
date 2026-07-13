namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Ambient per-import accumulator for the queue processor: phase timings (the
/// "blue" download phase and the "green" on-add verify phase of the progress
/// bar) and per-provider byte/article counts per phase. Flows via AsyncLocal,
/// so the byte hooks in CountingYencStream and the pipelined-STAT accounting
/// attribute to the right import automatically during processing, and are
/// no-ops everywhere else (WebDAV streaming, background health checks).
/// </summary>
public sealed class ImportStatsCollector
{
    public static readonly AsyncLocal<ImportStatsCollector?> Ambient = new();

    public static IDisposable BeginScope(ImportStatsCollector collector)
    {
        var previous = Ambient.Value;
        Ambient.Value = collector;
        return new ScopeReleaser(() => Ambient.Value = previous);
    }

    private sealed class ScopeReleaser(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    public sealed class ProviderCounters
    {
        public long DownloadBytes;
        public long VerifyBytes;
        public int Articles;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, ProviderCounters> _providers = new();
    private bool _inVerifyPhase;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DownloadEndedAt { get; private set; }
    public DateTimeOffset? VerifyEndedAt { get; private set; }

    public void AddBytes(string provider, long count)
    {
        if (count <= 0) return;
        lock (_lock)
        {
            var counters = GetCounters(provider);
            if (_inVerifyPhase) counters.VerifyBytes += count;
            else counters.DownloadBytes += count;
        }
    }

    public void AddArticle(string provider)
    {
        lock (_lock) GetCounters(provider).Articles++;
    }

    /// <summary>Blue phase over; any further bytes belong to the verify phase.</summary>
    public void MarkDownloadEnded()
    {
        lock (_lock)
        {
            _inVerifyPhase = true;
            DownloadEndedAt ??= DateTimeOffset.UtcNow;
        }
    }

    public void MarkVerifyEnded()
    {
        lock (_lock) VerifyEndedAt ??= DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<(string Provider, ProviderCounters Counters)> Snapshot()
    {
        lock (_lock)
        {
            return _providers
                .Select(kv => (kv.Key, new ProviderCounters
                {
                    DownloadBytes = kv.Value.DownloadBytes,
                    VerifyBytes = kv.Value.VerifyBytes,
                    Articles = kv.Value.Articles,
                }))
                .OrderByDescending(x => x.Item2.DownloadBytes + x.Item2.VerifyBytes)
                .ToList();
        }
    }

    private ProviderCounters GetCounters(string provider)
    {
        if (!_providers.TryGetValue(provider, out var counters))
            _providers[provider] = counters = new ProviderCounters();
        return counters;
    }
}
