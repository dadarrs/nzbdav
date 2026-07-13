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
    /// when the "use backup providers for health checks" setting is on -- providers of type
    /// "Backup &amp; Health Checks". STAT existence checks transfer no article bytes, so they
    /// cost block accounts almost nothing (only protocol traffic).
    /// </summary>
    public int TotalStatCheckConnections(bool useBackupProviders) => Math.Max(1, Providers
        .Where(x => x.IsStatCheckEligible(useBackupProviders))
        .Select(x => x.MaxConnections)
        .Sum());

    /// <summary>
    /// Per-account identity used for metrics attribution and display, aligned by index with
    /// Providers: the nickname when set, otherwise the host -- deduplicated in list order as
    /// host, host2, host3... so multiple accounts on the same backbone stay distinguishable.
    /// A single unnamed provider keeps its plain hostname, preserving continuity with
    /// metrics recorded before this existed.
    /// </summary>
    public List<string> GetEffectiveNames()
    {
        var names = new List<string>(Providers.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in Providers)
        {
            var baseName = string.IsNullOrWhiteSpace(provider.Nickname)
                ? provider.Host
                : provider.Nickname.Trim();
            var name = baseName;
            var suffix = 2;
            while (!used.Add(name)) name = $"{baseName}{suffix++}";
            names.Add(name);
        }

        return names;
    }

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

        // Optional user-friendly label shown in the UI in place of Host. Host is
        // still the real NNTP target and the stable key used for metrics/logs.
        public string? Nickname { get; set; }

        // null or 0 = no cap. Used by block-account holders to stop a paid block
        // from being drained beyond its purchased size.
        public long? ByteLimit { get; set; }

        // bytes added to the computed usage. Lets the user seed a starting value
        // when migrating from another client, or adjust drift against the
        // provider's own portal. Set to 0 after a reset.
        public long BytesUsedOffset { get; set; }

        // unix-ms cutoff: ProviderHourly rows older than this don't contribute to
        // the live counter. A reset bumps this to "now" so the gauge starts fresh
        // without losing the historical metrics rows underneath.
        public long BytesUsedResetAt { get; set; }

        // Pooled providers always serve health checks. Backup providers join the STAT fan-out
        // only when the global "use backup providers for health checks" setting is on AND the
        // provider's type is "Backup & Health Checks" -- the type is the sole gate, so plain
        // "Backup Only" means backup only. The pipelining tickbox merely selects pipelined vs
        // one-at-a-time STATs for providers that are already eligible by type.
        public bool IsStatCheckEligible(bool useBackupProviders) => Type == ProviderType.Pooled
            || (useBackupProviders && Type == ProviderType.BackupAndStats);
    }
}