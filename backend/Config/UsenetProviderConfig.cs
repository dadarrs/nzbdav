using NzbWebDAV.Models;

namespace NzbWebDAV.Config;

public class UsenetProviderConfig
{
    public List<ConnectionDetails> Providers { get; set; } = [];

    public int TotalPooledConnections => Math.Max(1, Providers
        .Where(x => x.Type == ProviderType.Pooled)
        .Select(x => x.MaxConnections)
        .Sum());

    /// <summary>
    /// Connections eligible to carry STAT health-check traffic: every pooled provider, plus --
    /// when the "use backup providers for health checks" setting is on -- any enabled backup
    /// provider that has STAT pipelining enabled. STAT existence checks transfer no article
    /// bytes, so they cost block accounts almost nothing (only protocol traffic).
    /// </summary>
    public int TotalStatCheckConnections(bool useBackupProviders) => Math.Max(1, Providers
        .Where(x => x.IsStatCheckEligible(useBackupProviders))
        .Select(x => x.MaxConnections)
        .Sum());

    public class ConnectionDetails
    {
        public required ProviderType Type { get; set; }
        public required string Host { get; set; }
        public required int Port { get; set; }
        public required bool UseSsl { get; set; }
        public required string User { get; set; }
        public required string Pass { get; set; }
        public required int MaxConnections { get; set; }

        // Whether STAT existence checks may be pipelined for this provider. Not "required" so
        // that provider configs saved before this feature existed deserialize to false (i.e.
        // linear STAT) and only opt in once the user has tested + enabled pipelining.
        public bool StatPipeliningEnabled { get; set; } = false;

        // Pooled providers always serve health checks. Backup providers join the STAT fan-out
        // only when the global "use backup providers for health checks" setting is on AND the
        // provider opted in: either its type is "Backup & Health Checks" (linear STATs, no
        // pipelining required) or its pipelining tickbox is enabled. Either opt-in is
        // per-provider, so a provider whose STAT support is broken can be left out.
        public bool IsStatCheckEligible(bool useBackupProviders) => Type == ProviderType.Pooled
            || (useBackupProviders && Type != ProviderType.Disabled
                                   && (StatPipeliningEnabled || Type == ProviderType.BackupAndStats));
    }
}