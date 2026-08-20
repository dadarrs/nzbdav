using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Tracks consecutive connection failures for an NNTP provider and temporarily
/// disables it when a failure threshold is reached, preventing a single
/// misbehaving provider from blocking the entire download pipeline.
/// <para>
/// After tripping, the provider enters a cooldown period during which it is
/// skipped. When the cooldown expires, a single probe attempt is allowed.
/// If the probe succeeds, the breaker resets. If it fails, the cooldown
/// doubles (up to a cap) and the breaker re-trips.
/// </para>
/// </summary>
public class ProviderCircuitBreaker
{
    private const int FailureThreshold = 3;
    private static readonly TimeSpan InitialCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);

    private readonly string _providerName;
    private readonly object _lock = new();

    private int _consecutiveFailures;
    private long _trippedUntilMs;
    private TimeSpan _currentCooldown = InitialCooldown;
    private long _generation;
    private bool _probeInProgress;

    public ProviderCircuitBreaker(string providerName)
    {
        _providerName = providerName;
    }

    public bool IsTripped
    {
        get
        {
            lock (_lock)
            {
                if (_trippedUntilMs == 0) return false;
                return Environment.TickCount64 < _trippedUntilMs || _probeInProgress;
            }
        }
    }

    /// <summary>
    /// Admits normal work while closed. Once the cooldown has elapsed, exactly one caller
    /// is admitted as a half-open probe; all others must use another provider.
    /// </summary>
    public bool TryEnter(out long generation)
    {
        lock (_lock)
        {
            generation = _generation;
            if (_trippedUntilMs == 0) return true;
            if (Environment.TickCount64 < _trippedUntilMs || _probeInProgress) return false;

            _probeInProgress = true;
            return true;
        }
    }

    public void RecordSuccess(long generation)
    {
        lock (_lock)
        {
            // Ignore stale operations admitted before a newer failure wave tripped the breaker.
            if (generation != _generation) return;

            if (_consecutiveFailures > 0 || _trippedUntilMs > 0)
                Log.Information("Provider {Provider} recovered — circuit breaker reset.", _providerName);

            _consecutiveFailures = 0;
            _trippedUntilMs = 0;
            _currentCooldown = InitialCooldown;
            _probeInProgress = false;
        }
    }

    public void RecordFailure(long generation)
    {
        lock (_lock)
        {
            // Once one operation trips the breaker, the rest of that already-admitted wave
            // must not repeatedly re-trip it, extend cooldown, or overwrite the probe state.
            if (generation != _generation) return;

            _probeInProgress = false;
            _consecutiveFailures++;

            if (_consecutiveFailures < FailureThreshold) return;

            _trippedUntilMs = Environment.TickCount64 + (long)_currentCooldown.TotalMilliseconds;
            Log.Warning(
                "Provider {Provider} tripped after {Failures} consecutive failures. " +
                "Skipping for {Cooldown}s.",
                _providerName, _consecutiveFailures, _currentCooldown.TotalSeconds);

            _currentCooldown = TimeSpan.FromMilliseconds(
                Math.Min(_currentCooldown.TotalMilliseconds * 2, MaxCooldown.TotalMilliseconds));
            _generation++;
        }
    }

    /// <summary>Releases a half-open probe admission that was cancelled by its caller.</summary>
    public void RecordCancellation(long generation)
    {
        lock (_lock)
        {
            if (generation == _generation)
                _probeInProgress = false;
        }
    }
}
