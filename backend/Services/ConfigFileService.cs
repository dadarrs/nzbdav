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
        "Live export of nzbdav settings, grouped into sections (api, usenet, webdav, ...).",
        "Edit and save: changes apply within seconds while nzbdav runs, or at next startup.",
        "Keys starting with _ are informational and ignored on import.",
        "Values may reference container environment variables as ${VAR}, e.g. \"Pass\": \"${PASS}\".",
        "References stay literal in this file and resolve when settings are read, so secrets can live in your .env.",
        "The env section overrides environment variables: add \"NAME\": \"value\" there (empty value = unset).",
        "This file contains passwords and API keys -- redact before sharing.",
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
            if (File.Exists(FilePath)) await ImportAsync(stoppingToken).ConfigureAwait(false);
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

            // Group keys into sections by their first dot segment ("usenet.providers"
            // lands in section "usenet" as "providers"), so related settings sit
            // together under a visible heading. Keys without a dot stay top-level.
            // configItems arrive sorted, so sections come out alphabetical too.
            foreach (var item in configItems)
            {
                var dot = item.ConfigName.IndexOf('.');
                if (dot <= 0)
                {
                    root[item.ConfigName] = ToFileValue(item.ConfigValue);
                    continue;
                }

                var sectionName = item.ConfigName[..dot];
                if (root[sectionName] is not JsonObject section)
                {
                    section = new JsonObject();
                    root[sectionName] = section;
                }

                section[item.ConfigName[(dot + 1)..]] = ToFileValue(item.ConfigValue);
            }

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

    private async Task ImportAsync(CancellationToken ct)
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
                return;
            }

            // Accepts both shapes: sectioned ("usenet": { "providers": ... }) and flat
            // ("usenet.providers": ...). A dotless key with an object value is a section
            // to flatten; anything else is a config key as-is -- unambiguous because
            // real config keys always contain a dot, section names never do.
            var fileValues = new Dictionary<string, string>();
            foreach (var (key, node) in root)
            {
                if (key.StartsWith('_')) continue;
                if (node is JsonObject section && !key.Contains('.'))
                {
                    foreach (var (childKey, childNode) in section)
                    {
                        if (childKey.StartsWith('_')) continue;
                        fileValues[$"{key}.{childKey}"] = FromFileValue(childNode);
                    }
                }
                else
                {
                    fileValues[key] = FromFileValue(node);
                }
            }
            if (fileValues.Count == 0) return;

            await using var dbContext = new DavDatabaseContext();
            var existingItems = await dbContext.ConfigItems
                .ToDictionaryAsync(x => x.ConfigName, ct).ConfigureAwait(false);

            var changedItems = new List<ConfigItem>();
            foreach (var (key, value) in fileValues)
            {
                if (existingItems.TryGetValue(key, out var existing))
                {
                    if (existing.ConfigValue == value) continue;
                    existing.ConfigValue = value;
                }
                else
                {
                    dbContext.ConfigItems.Add(new ConfigItem { ConfigName = key, ConfigValue = value });
                }

                changedItems.Add(new ConfigItem { ConfigName = key, ConfigValue = value });
            }

            if (changedItems.Count == 0) return;
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            configManager.UpdateValues(changedItems);
            Log.Information("Applied {Count} setting(s) from {Path}: {Keys}",
                changedItems.Count, FilePath, string.Join(", ", changedItems.Select(x => x.ConfigName)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to import settings from {Path}", FilePath);
        }
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
