using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Mirrors every ConfigItems row to an editable JSON file in the data folder
/// (config.json) so settings can be backed up, shared, and edited by hand.
///
/// Sync rules:
///   - Every settings save (ConfigManager.UpdateValues) rewrites the file.
///   - External edits to the file (terminal, editor, restored backup) are
///     detected via FileSystemWatcher plus a polling fallback and applied
///     through the same path a UI save uses, so dependent services rebuild.
///   - At startup, a file that differs from the database wins -- this is what
///     makes "drop a config.json in and start the container" restores work.
///     While running, whichever side changed last wins per save/edit.
///   - Keys present in the database but missing from the file are left alone
///     (an edit can add or change settings, never silently delete them).
///
/// Values that are JSON objects/arrays (usenet.providers, arr.instances) are
/// embedded as real JSON so they are hand-editable; on import they are
/// re-compacted to the string form ConfigItems stores. All other values stay
/// plain strings to keep round-trips byte-exact. Keys starting with "_" are
/// reserved for comments and skipped on import.
/// </summary>
public class ConfigFileService(ConfigManager configManager) : BackgroundService
{
    public static string FilePath => Path.Join(DavDatabaseContext.ConfigPath, "config.json");

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);

    private const string NoteKey = "_note";
    private const string ReadmeKey = "_readme";
    private const string EnvironmentKey = "_environment";

    // One short line per array entry -- JSON has no comments, so a string array
    // is the closest thing to a readable comment block at the top of the file.
    private static readonly string[] ReadmeLines =
    [
        "Live export of nzbdav settings. Sections mirror the WebUI Settings tabs, in tab order;",
        "settings within a section follow their on-screen order. extras holds settings with no",
        "UI surface, and env overrides environment variables (\"NAME\": \"value\", empty = unset).",
        "Edit and save: changes apply within seconds while nzbdav runs, or at next startup.",
        "Keys starting with _ are informational and ignored on import.",
        "Values may reference container environment variables as ${VAR}, e.g. \"Pass\": \"${PASS}\".",
        "References stay literal in this file and resolve when settings are read, so secrets can live in your .env.",
        "This file contains passwords and API keys -- redact before sharing.",
    ];

    // Mirrors the WebUI: one entry per Settings tab, in tab order, listing that
    // tab's config keys in on-screen order. Keys absent from this manifest land
    // in the trailing "extras" section. Keep in sync when the settings pages
    // gain or move fields.
    private static readonly (string Section, string[] Keys)[] UiSections =
    [
        ("usenet",
        [
            "usenet.providers",
        ]),
        ("sabnzbd",
        [
            "api.key",
            "api.categories",
            "api.manual-category",
            "api.import-strategy",
            "rclone.mount-dir",
            "api.completed-downloads-dir",
            "general.base-url",
            "api.download-file-blocklist",
            "api.duplicate-nzb-behavior",
            "api.user-agent",
            "api.ensure-importable-video",
            "api.ensure-article-existence-categories",
            "usenet.nntp-pipelining.enabled",
            "usenet.nntp-pipelining.depth",
            "api.backup-providers-for-health-checks",
            "api.backup-providers-for-background-health-checks",
        ]),
        ("webdav",
        [
            "webdav.user",
            "webdav.pass",
            "usenet.max-download-connections",
            "usenet.streaming-priority",
            "usenet.article-buffer-size",
            "webdav.enforce-readonly",
            "webdav.active-stream-tracker",
            "webdav.show-hidden-files",
            "webdav.preview-par2-files",
        ]),
        ("radarr-sonarr",
        [
            "arr.instances",
        ]),
        ("repairs",
        [
            "media.library-dir",
            "repair.enable",
        ]),
        ("rclone",
        [
            "rclone.host",
            "rclone.user",
            "rclone.pass",
            "rclone.rc-enabled",
        ]),
        ("maintenance",
        [
            "db.is-startup-vacuum-enabled",
            "maintenance.remove-orphaned-schedule-enabled",
            "maintenance.remove-orphaned-schedule-time",
        ]),
        ("system",
        [
            "ui.theme",
        ]),
    ];

    private static readonly string[] EnvironmentNoteLines =
    [
        "Informational: environment variables the app reads and their current values.",
        "Not imported -- to override one, add it to the env section instead.",
        "Read before config loads and therefore not overridable: {0}.",
    ];

    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private volatile bool _exportRequested;
    private string? _lastWrittenHash;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        configManager.OnConfigChanged += OnConfigChanged;
        using var watcher = TryCreateWatcher();
        try
        {
            // Startup reconcile: an existing file that differs from the database is
            // an offline edit or a restored backup -- apply it, then normalize.
            if (File.Exists(FilePath))
            {
                var applied = await ImportAsync(stoppingToken).ConfigureAwait(false);
                if (applied == 0)
                    Log.Information("Config file check: {Path} matches the database", FilePath);
                else
                    Log.Information(
                        "Config file check: applied {Count} setting(s) from {Path} (details above)",
                        applied, FilePath);
            }
            else
            {
                Log.Information("Config file check: {Path} not found; creating it from current settings", FilePath);
            }

            await ExportAsync(stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                var woke = await _wakeSignal.WaitAsync(PollInterval, stoppingToken).ConfigureAwait(false);
                if (woke) await Task.Delay(DebounceDelay, stoppingToken).ConfigureAwait(false);

                if (_exportRequested)
                {
                    _exportRequested = false;
                    await ExportAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Poll/watcher tick: anything we didn't write ourselves is an external edit.
                if (!File.Exists(FilePath))
                {
                    await ExportAsync(stoppingToken).ConfigureAwait(false);
                }
                else if (await ComputeFileHashAsync(stoppingToken).ConfigureAwait(false) != _lastWrittenHash)
                {
                    await ImportAsync(stoppingToken).ConfigureAwait(false);
                    await ExportAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            configManager.OnConfigChanged -= OnConfigChanged;
        }
    }

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs e)
    {
        _exportRequested = true;
        Wake();
    }

    private void Wake()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // a wake-up is already pending
        }
    }

    private FileSystemWatcher? TryCreateWatcher()
    {
        // Watch the directory, not the file: editors often save via write-to-temp
        // + rename, which replaces the watched inode. The polling loop is the
        // fallback when inotify is unavailable (some network mounts).
        try
        {
            var watcher = new FileSystemWatcher(DavDatabaseContext.ConfigPath, Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Changed += (_, _) => Wake();
            watcher.Created += (_, _) => Wake();
            watcher.Renamed += (_, _) => Wake();
            watcher.Deleted += (_, _) => Wake();
            return watcher;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Config-file watcher unavailable; falling back to {Seconds}s polling only.",
                PollInterval.TotalSeconds);
            return null;
        }
    }

    private async Task ExportAsync(CancellationToken ct)
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            var configItems = await dbContext.ConfigItems
                .OrderBy(x => x.ConfigName)
                .ToListAsync(ct).ConfigureAwait(false);

            var root = new JsonObject
            {
                [ReadmeKey] = new JsonArray(ReadmeLines.Select(l => (JsonNode)JsonValue.Create(l)).ToArray()),
                [EnvironmentKey] = BuildEnvironmentInfo(),
            };

            // WebUI-shaped sections: tabs in tab order, keys in on-screen order (full
            // config keys, since a tab can mix prefixes). Whatever the manifest doesn't
            // claim goes to "env" (overrides, as bare variable names) and "extras".
            var remaining = configItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue);
            foreach (var (sectionName, keys) in UiSections)
            {
                var section = new JsonObject();
                foreach (var key in keys)
                {
                    if (remaining.Remove(key, out var value))
                        section[key] = ToFileValue(value);
                }

                if (section.Count > 0) root[sectionName] = section;
            }

            var envSection = new JsonObject();
            foreach (var key in remaining.Keys
                         .Where(k => k.StartsWith(EnvOverrides.Prefix, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList())
            {
                remaining.Remove(key, out var value);
                envSection[key[EnvOverrides.Prefix.Length..]] = ToFileValue(value ?? "");
            }

            if (envSection.Count > 0) root["env"] = envSection;

            var extras = new JsonObject();
            foreach (var key in remaining.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList())
                extras[key] = ToFileValue(remaining[key]);
            if (extras.Count > 0) root["extras"] = extras;

            // Relaxed escaping keeps quotes readable in the _readme lines; the file is
            // consumed by humans and this service only, never embedded in HTML.
            var json = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n";
            var bytes = Encoding.UTF8.GetBytes(json);

            // Atomic replace so a concurrent reader never sees a half-written file.
            var tempPath = FilePath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, bytes, ct).ConfigureAwait(false);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tempPath, FilePath, overwrite: true);

            _lastWrittenHash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to export settings to {Path}", FilePath);
        }
    }

    /// <summary>Returns the number of settings applied from the file (0 = in sync or unreadable).</summary>
    private async Task<int> ImportAsync(CancellationToken ct)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(FilePath, ct).ConfigureAwait(false);

            JsonObject root;
            try
            {
                root = JsonNode.Parse(bytes) as JsonObject
                       ?? throw new JsonException("Top-level value must be a JSON object.");
            }
            catch (JsonException ex)
            {
                // Leave the file untouched so the user can fix the typo; the next
                // change event (or poll tick) retries.
                Log.Warning("Ignoring invalid {Path}: {Message}", FilePath, ex.Message);
                _lastWrittenHash = Convert.ToHexString(SHA256.HashData(bytes));
                return 0;
            }

            // Accepts every shape this file has ever had: WebUI-tab sections with full
            // config keys as children, prefix sections with suffix children (childKey
            // without a dot joins the section name, which is also how bare names in the
            // env section become env.NAME), and plain flat keys at the top level. A
            // dotless top-level key with an object value is a section to flatten;
            // anything else is a config key as-is -- unambiguous because real config
            // keys always contain a dot and section names never do.
            var fileValues = new Dictionary<string, string>();
            foreach (var (key, node) in root)
            {
                if (key.StartsWith('_')) continue;
                if (node is JsonObject section && !key.Contains('.'))
                {
                    foreach (var (childKey, childNode) in section)
                    {
                        if (childKey.StartsWith('_')) continue;
                        var configKey = childKey.Contains('.') || key is "extras" or "radarr-sonarr"
                            ? childKey
                            : $"{key}.{childKey}";
                        fileValues[configKey] = FromFileValue(childNode);
                    }
                }
                else
                {
                    fileValues[key] = FromFileValue(node);
                }
            }
            if (fileValues.Count == 0) return 0;

            await using var dbContext = new DavDatabaseContext();
            var existingItems = await dbContext.ConfigItems
                .ToDictionaryAsync(x => x.ConfigName, ct).ConfigureAwait(false);

            var changedItems = new List<ConfigItem>();
            foreach (var (key, value) in fileValues)
            {
                string? previousValue = null;
                if (existingItems.TryGetValue(key, out var existing))
                {
                    if (existing.ConfigValue == value) continue;
                    previousValue = existing.ConfigValue;
                    existing.ConfigValue = value;
                }
                else
                {
                    dbContext.ConfigItems.Add(new ConfigItem { ConfigName = key, ConfigValue = value });
                }

                Log.Information("Config file: {Key} changed: {Old} -> {New}",
                    key,
                    previousValue == null ? "(not set)" : DescribeValue(key, previousValue),
                    DescribeValue(key, value));
                changedItems.Add(new ConfigItem { ConfigName = key, ConfigValue = value });
            }

            if (changedItems.Count == 0) return 0;
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            configManager.UpdateValues(changedItems);
            Log.Information("Applied {Count} setting(s) from {Path}: {Keys}",
                changedItems.Count, FilePath, string.Join(", ", changedItems.Select(x => x.ConfigName)));
            return changedItems.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to import settings from {Path}", FilePath);
            return 0;
        }
    }

    /// <summary>
    /// Renders a config value for the change log. Secrets are obfuscated: keys that
    /// look credential-bearing show dots (unless the value is a pure ${VAR} reference,
    /// which is safe and informative to show), and JSON blobs -- which can carry
    /// passwords in their fields -- never log their contents.
    /// </summary>
    private static string DescribeValue(string key, string value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";

        var trimmed = value.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return "(structured value; contents not logged)";

        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^\$\{[A-Za-z_][A-Za-z0-9_]*\}$"))
            return value;

        var sensitive = key.Contains("pass", StringComparison.OrdinalIgnoreCase)
                        || key.Contains("key", StringComparison.OrdinalIgnoreCase)
                        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
                        || key.Contains("token", StringComparison.OrdinalIgnoreCase);
        if (sensitive) return "•••";

        return value.Length <= 80 ? value : value[..77] + "...";
    }

    private static JsonObject BuildEnvironmentInfo()
    {
        var noteLines = EnvironmentNoteLines
            .Select(line => (JsonNode)JsonValue.Create(
                string.Format(line, string.Join(", ", EnvOverrides.ReadBeforeConfigLoads))))
            .ToArray();
        var env = new JsonObject { [NoteKey] = new JsonArray(noteLines) };
        foreach (var name in EnvOverrides.KnownVariables)
            env[name] = Environment.GetEnvironmentVariable(name);
        return env;
    }

    /// <summary>
    /// Object/array values are embedded as real JSON so they're hand-editable;
    /// everything else stays a plain string so round-trips are byte-exact.
    /// </summary>
    private static JsonNode? ToFileValue(string configValue)
    {
        var trimmed = configValue.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                return JsonNode.Parse(configValue);
            }
            catch (JsonException)
            {
                // not valid JSON after all -- store as a plain string
            }
        }

        return JsonValue.Create(configValue);
    }

    /// <summary>
    /// Inverse of <see cref="ToFileValue"/>. Objects/arrays re-compact to the
    /// string form ConfigItems stores; bare numbers/booleans a hand-edit may
    /// have introduced become their literal text ("true", "15"), which matches
    /// how the string-typed settings are parsed.
    /// </summary>
    private static string FromFileValue(JsonNode? node)
    {
        if (node == null) return "";
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        return node.ToJsonString();
    }

    private async Task<string> ComputeFileHashAsync(CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(FilePath, ct).ConfigureAwait(false);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public override void Dispose()
    {
        _wakeSignal.Dispose();
        base.Dispose();
    }
}
