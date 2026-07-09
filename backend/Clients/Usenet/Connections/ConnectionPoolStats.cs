using NzbWebDAV.Config;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Clients.Usenet.Connections;

public class ConnectionPoolStats
{
    private readonly int[] _live;
    private readonly int[] _idle;
    private readonly UsenetProviderConfig _providerConfig;
    private readonly Func<bool> _useBackupProvidersForStatChecks;
    private readonly WebsocketManager _websocketManager;

    public ConnectionPoolStats
    (
        UsenetProviderConfig providerConfig,
        Func<bool> useBackupProvidersForStatChecks,
        WebsocketManager websocketManager
    )
    {
        var count = providerConfig.Providers.Count;
        _live = new int[count];
        _idle = new int[count];
        _providerConfig = providerConfig;
        _useBackupProvidersForStatChecks = useBackupProvidersForStatChecks;
        _websocketManager = websocketManager;
    }

    public EventHandler<ConnectionPoolChangedEventArgs> GetOnConnectionPoolChanged(int providerIndex)
    {
        return OnEvent;

        void OnEvent(object? _, ConnectionPoolChangedEventArgs args)
        {
            // Totals cover every provider whose connections can carry work for the pool as a
            // whole: pooled providers plus backups opted into STAT health checks. Eligibility is
            // recomputed per event so toggling the backup-providers setting updates the display
            // without a provider-list change.
            int totalLive, totalIdle, max;
            lock (this)
            {
                _live[providerIndex] = args.Live;
                _idle[providerIndex] = args.Idle;

                var useBackupProviders = _useBackupProvidersForStatChecks();
                totalLive = 0;
                totalIdle = 0;
                max = 0;
                for (var i = 0; i < _providerConfig.Providers.Count; i++)
                {
                    if (!_providerConfig.Providers[i].IsStatCheckEligible(useBackupProviders)) continue;
                    totalLive += _live[i];
                    totalIdle += _idle[i];
                    max += _providerConfig.Providers[i].MaxConnections;
                }
            }

            var message = $"{providerIndex}|{args.Live}|{args.Idle}|{totalLive}|{max}|{totalIdle}";
            _websocketManager.SendMessage(WebsocketTopic.UsenetConnections, message);
        }
    }

    public sealed class ConnectionPoolChangedEventArgs(int live, int idle, int max) : EventArgs
    {
        public int Live { get; } = live;
        public int Idle { get; } = idle;
        public int Max { get; } = max;
        public int Active => Live - Idle;
    }
}
