using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    public static readonly string AppVersion = EnvironmentUtil.GetEnvironmentVariable("NZBDAV_VERSION") ?? "unknown";

    private readonly Dictionary<string, string> _config = new();
    public event EventHandler<ConfigEventArgs>? OnConfigChanged;

    public async Task LoadConfig()
    {
        await using var dbContext = new DavDatabaseContext();
        var configItems = await dbContext.ConfigItems.ToListAsync().ConfigureAwait(false);
        lock (_config)
        {
            _config.Clear();
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }
    }

    private string? GetRawConfigValue(string configName)
    {
        lock (_config)
        {
            return _config.TryGetValue(configName, out string? value) ? value : null;
        }
    }

    // ${VAR} references expand to environment values at read time; the stored
    // value keeps the literal reference so exports/imports round-trip it.
    private string? GetConfigValue(string configName)
    {
        var rawValue = GetRawConfigValue(configName);
        return rawValue == null ? null : EnvExpansion.Expand(rawValue);
    }

    private T? GetConfigValue<T>(string configName)
    {
        var rawValue = StringUtil.EmptyToNull(GetRawConfigValue(configName));
        if (rawValue == null) return default;
        if (!rawValue.Contains("${")) return JsonSerializer.Deserialize<T>(rawValue);
        // JSON-aware expansion: substitute inside string leaves only, so secrets
        // containing quotes/backslashes can't corrupt the document.
        var expanded = EnvExpansion.ExpandNode(JsonNode.Parse(rawValue));
        return expanded == null ? default : expanded.Deserialize<T>();
    }

    /// <summary>
    /// All current config entries whose key starts with the given prefix.
    /// Used by the env.* override mechanism to seed process environment variables.
    /// </summary>
    public List<KeyValuePair<string, string>> GetValuesWithPrefix(string prefix)
    {
        lock (_config)
        {
            return _config
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        lock (_config)
        {
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }

        var changedConfig = configItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue);
        OnConfigChanged?.Invoke(this, new ConfigEventArgs { ChangedConfig = changedConfig });
    }

    public string GetRcloneMountDir()
    {
        var mountDir = StringUtil.EmptyToNull(GetConfigValue("rclone.mount-dir"))
                       ?? EnvironmentUtil.GetEnvironmentVariable("MOUNT_DIR")
                       ?? "/mnt/nzbdav";
        if (mountDir.EndsWith('/')) mountDir = mountDir.TrimEnd('/');
        return mountDir;
    }

    public string GetApiKey()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.key"))
               ?? EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetStrmKey()
    {
        return GetConfigValue("api.strm-key")
               ?? throw new InvalidOperationException("The `api.strm-key` config does not exist.");
    }

    public List<string> GetApiCategories()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("api.categories"))
                    ?? EnvironmentUtil.GetEnvironmentVariable("CATEGORIES")
                    ?? "audio,software,tv,movies";

        return value.Split(',')
            .Prepend(GetManualUploadCategory())
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public string GetManualUploadCategory()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.manual-category"))
               ?? "uncategorized";
    }

    public string? GetWebdavUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue("webdav.user"))
               ?? EnvironmentUtil.GetEnvironmentVariable("WEBDAV_USER")
               ?? "admin";
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = StringUtil.EmptyToNull(GetConfigValue("webdav.pass"));
        if (hashedPass != null) return hashedPass;
        var pass = EnvironmentUtil.GetEnvironmentVariable("WEBDAV_PASSWORD");
        if (pass != null) return PasswordUtil.Hash(pass);
        return null;
    }

    public bool IsEnsureImportableVideoEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ensure-importable-video"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool ShowHiddenWebdavFiles()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.show-hidden-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetLibraryDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue("media.library-dir"));
    }

    public int GetMaxDownloadConnections()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.max-download-connections"))
            ?? Math.Min(GetUsenetProviderConfig().TotalPooledConnections, 15).ToString()
        );
    }

    /// <summary>
    /// Health checks need enough parallelism to hide NNTP latency, but using the sum of every
    /// provider's connection allowance can turn one network fault into hundreds of simultaneous
    /// reconnects. Keep a separate, configurable ceiling; pipelining still provides high STAT
    /// throughput at the conservative default.
    /// </summary>
    public int GetHealthCheckConcurrency(bool useBackupProviders)
    {
        var available = GetUsenetProviderConfig().TotalStatCheckConnections(useBackupProviders);
        if (available <= 1) return 1;

        var configured = StringUtil.EmptyToNull(GetConfigValue("usenet.health-check-concurrency"));
        var requested = configured != null ? int.Parse(configured) : Math.Min(available, 32);
        return Math.Clamp(requested, 1, available);
    }

    public int GetArticleBufferSize()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.article-buffer-size"))
            ?? "40"
        );
    }

    public SemaphorePriorityOdds GetStreamingPriority()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("usenet.streaming-priority"));
        var numericalValue = int.Parse(stringValue ?? "80");
        return new SemaphorePriorityOdds() { HighPriorityOdds = numericalValue };
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.enforce-readonly"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public HashSet<string> GetEnsureArticleExistenceCategories()
    {
        var configValue = GetConfigValue("api.ensure-article-existence-categories");
        return (configValue ?? "").Split(',')
            .Select(x => x.Trim())
            .Select(x => x.ToLower())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet();
    }

    /// <summary>
    /// When enabled, providers of type "Backup &amp; Health Checks" carry article health-check
    /// traffic alongside the pooled providers (type is the sole gate; plain "Backup Only"
    /// providers never do). STAT checks transfer no article bytes; on byte-metered blocks the
    /// protocol traffic still counts, at roughly 0.7MB of quota per 10GB of content checked
    /// (measured ~45 bytes per STAT).
    /// </summary>
    public bool UseBackupProvidersForHealthChecks()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.backup-providers-for-health-checks"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    /// <summary>
    /// Scopes backup-provider health checks: when disabled (the default), backup providers only
    /// carry the on-add check (once per import) and periodic background library re-scans stay on
    /// pooled providers, so block quotas are not drained continuously. Only meaningful while
    /// UseBackupProvidersForHealthChecks is enabled.
    /// </summary>
    public bool UseBackupProvidersForBackgroundHealthChecks()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.backup-providers-for-background-health-checks"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    /// <summary>
    /// Unix-ms watermark set by "Clear history" on the overview stream-history panel.
    /// Read sessions that ended at or before this instant are hidden from the history
    /// list but keep contributing to dashboard statistics (rows are not deleted).
    /// </summary>
    public long GetStreamHistoryClearedBefore()
    {
        var configValue = StringUtil.EmptyToNull(GetConfigValue("metrics.stream-history-cleared-before"));
        return configValue != null && long.TryParse(configValue, out var value) ? value : 0L;
    }

    /// <summary>
    /// Per-step NNTP deadline (connect, each command write / response read).
    /// File/config editable only (usenet.timeouts.command-seconds); no UI surface.
    /// </summary>
    public TimeSpan GetNntpCommandTimeout()
    {
        return TimeSpan.FromSeconds(Math.Clamp(GetTimeoutSeconds("usenet.timeouts.command-seconds") ?? 10, 2, 300));
    }

    /// <summary>
    /// Deadline for the TLS handshake specifically. null = same as the command timeout.
    /// </summary>
    public TimeSpan? GetNntpTlsHandshakeTimeout()
    {
        var seconds = GetTimeoutSeconds("usenet.timeouts.tls-handshake-seconds");
        return seconds == null ? null : TimeSpan.FromSeconds(Math.Clamp(seconds.Value, 2, 300));
    }

    /// <summary>
    /// Idle gap allowed between consecutive replies of a pipelined STAT batch.
    /// null = twice the command timeout.
    /// </summary>
    public TimeSpan? GetNntpStatPipelineIdleTimeout()
    {
        var seconds = GetTimeoutSeconds("usenet.timeouts.stat-pipeline-idle-seconds");
        return seconds == null ? null : TimeSpan.FromSeconds(Math.Clamp(seconds.Value, 2, 300));
    }

    private int? GetTimeoutSeconds(string configName)
    {
        var configValue = StringUtil.EmptyToNull(GetConfigValue(configName));
        return configValue != null && int.TryParse(configValue, out var value) && value > 0 ? value : null;
    }

    /// <summary>
    /// Master switch for NNTP STAT pipelining. When disabled, STAT health checks always run
    /// one-command-per-round-trip regardless of any provider's individual pipelining setting.
    /// </summary>
    public bool GetNntpPipeliningEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("usenet.nntp-pipelining.enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    /// <summary>
    /// The maximum number of STAT commands sent back-to-back before reading their responses.
    /// A value of 1 (or less) effectively disables pipelining. Bounded to a conservative range:
    /// the benefit flattens out well before 150 and higher depths only add provider risk for
    /// fractions of a second.
    /// </summary>
    public int GetNntpPipeliningDepth()
    {
        var defaultValue = 50;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("usenet.nntp-pipelining.depth"));
        var depth = configValue != null ? int.Parse(configValue) : defaultValue;
        return Math.Clamp(depth, 1, 150);
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.preview-par2-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsActiveStreamTrackerEnabled()
    {
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.active-stream-tracker"));
        return configValue == null || !bool.TryParse(configValue, out var result) || result;
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ignore-history-limit"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsRepairJobEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.enable"));
        var isRepairJobEnabled = (configValue != null ? bool.Parse(configValue) : defaultValue);
        return isRepairJobEnabled
               && GetLibraryDir() != null
               && GetArrConfig().GetInstanceCount() > 0;
    }

    public ArrConfig GetArrConfig()
    {
        var defaultValue = new ArrConfig();
        return GetConfigValue<ArrConfig>("arr.instances") ?? defaultValue;
    }

    public UsenetProviderConfig GetUsenetProviderConfig()
    {
        var defaultValue = new UsenetProviderConfig();
        return GetConfigValue<UsenetProviderConfig>("usenet.providers") ?? defaultValue;
    }

    public string GetDuplicateNzbBehavior()
    {
        var defaultValue = "increment";
        return GetConfigValue("api.duplicate-nzb-behavior") ?? defaultValue;
    }

    public HashSet<string> GetBlocklistedFiles()
    {
        var defaultValue = "*.nfo, *.par2, *.sfv, *sample.mkv";
        return (GetConfigValue("api.download-file-blocklist") ?? defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLower())
            .ToHashSet();
    }

    public string GetImportStrategy()
    {
        return GetConfigValue("api.import-strategy") ?? "symlinks";
    }

    public string GetStrmCompletedDownloadDir()
    {
        return GetConfigValue("api.completed-downloads-dir") ?? "/data/completed-downloads";
    }

    public string GetBaseUrl()
    {
        return GetConfigValue("general.base-url") ?? "http://localhost:3000";
    }

    public bool IsRcloneRemoteControlEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("rclone.rc-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetRcloneHost()
    {
        return GetConfigValue("rclone.host");
    }

    public string? GetRcloneUser()
    {
        return GetConfigValue("rclone.user");
    }

    public string? GetRclonePass()
    {
        return GetConfigValue("rclone.pass");
    }

    public string GetUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return StringUtil.EmptyToNull(GetConfigValue("api.user-agent"))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_GRAB_USER_AGENT")
               ?? defaultValue;
    }

    public bool IsDatabaseStartupVacuumEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("db.is-startup-vacuum-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsNzbBackupEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.nzb-backup-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetNzbBackupLocation()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.nzb-backup-location"));
    }

    public bool IsRemoveOrphanedFilesScheduleEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("maintenance.remove-orphaned-schedule-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public TimeSpan RemoveOrphanedFilesSchedule()
    {
        var defaultValue = TimeSpan.Zero;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("maintenance.remove-orphaned-schedule-time"));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var totalMinutes)) return defaultValue;
        if (totalMinutes < 0 || totalMinutes >= 24 * 60) return defaultValue;
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public class ConfigEventArgs : EventArgs
    {
        public required Dictionary<string, string> ChangedConfig { get; init; }
    }
}
