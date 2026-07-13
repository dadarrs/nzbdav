using Serilog;

namespace NzbWebDAV.Config;

/// <summary>
/// Lets config entries override the process environment: any config key of the
/// form "env.&lt;VARIABLE&gt;" is written into the process environment at startup
/// (after config load) and whenever it changes. Because it mutates the real
/// process environment, it also reaches code that reads variables directly at
/// use time (e.g. the NNTP_TLS_* checks inside UsenetSharp at connect time).
///
/// Variables consumed before config loads (bootstrap paths, logging, version)
/// cannot be overridden this way -- see <see cref="ReadBeforeConfigLoads"/>.
/// An empty value removes the variable from the environment.
/// </summary>
public static class EnvOverrides
{
    public const string Prefix = "env.";

    /// <summary>
    /// Every environment variable the app reads, for the config-file's
    /// informational listing. Keep in sync when adding env-var reads.
    /// </summary>
    public static readonly string[] KnownVariables =
    [
        "CATEGORIES",
        "CONFIG_PATH",
        "FRONTEND_BACKEND_API_KEY",
        "LOG_BUFFER_SIZE",
        "LOG_LEVEL",
        "MAX_REQUEST_BODY_SIZE",
        "MOUNT_DIR",
        "NNTP_TLS_IGNORE_CERT_DOMAINS",
        "NNTP_TLS_IGNORE_NAME_MISMATCH",
        "NZBDAV_VERSION",
        "NZB_GRAB_USER_AGENT",
        "UPGRADE",
        "WEBDAV_PASSWORD",
        "WEBDAV_USER",
    ];

    /// <summary>
    /// Consumed during bootstrap, before the config (and therefore any env.*
    /// override) is loaded. Overriding these via config has no effect.
    /// </summary>
    public static readonly string[] ReadBeforeConfigLoads =
    [
        "CONFIG_PATH",
        "LOG_BUFFER_SIZE",
        "LOG_LEVEL",
        "NZBDAV_VERSION",
        "UPGRADE",
    ];

    public static void ApplyAll(ConfigManager configManager)
    {
        foreach (var (key, value) in configManager.GetValuesWithPrefix(Prefix))
            Apply(key, value);
    }

    public static void ApplyChanged(IReadOnlyDictionary<string, string> changedConfig)
    {
        foreach (var (key, value) in changedConfig)
        {
            if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            Apply(key, value);
        }
    }

    private static void Apply(string configKey, string value)
    {
        var variable = configKey[Prefix.Length..].Trim();
        if (variable.Length == 0) return;
        // ${VAR} references resolve against the current environment first, so an
        // override can alias another variable. null removes the variable; an
        // empty config value means "unset".
        var expanded = EnvExpansion.Expand(value);
        Environment.SetEnvironmentVariable(variable, string.IsNullOrEmpty(expanded) ? null : expanded);
        Log.Information("Environment override applied: {Variable}={Value}",
            variable, string.IsNullOrEmpty(expanded) ? "(unset)" : expanded);
    }
}
