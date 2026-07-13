using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NzbWebDAV.Config;

/// <summary>
/// Expands ${VAR} references to environment-variable values inside config
/// entries, so secrets can live in the container environment (.env /
/// docker-compose) while config.json holds only references.
///
/// Expansion happens lazily at READ time (ConfigManager getters), never at
/// import or save: the database and the config file both keep the literal
/// "${VAR}" text, which is what lets the reference survive export/import
/// round-trips and keeps the secret out of both files. Unknown variables are
/// left as-is so a typo surfaces visibly instead of silently blanking a
/// password. "$${VAR}" escapes to a literal "${VAR}".
/// </summary>
public static partial class EnvExpansion
{
    [GeneratedRegex(@"(?<!\$)\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex ReferencePattern();

    public static string Expand(string value)
    {
        if (!value.Contains("${")) return value;
        var expanded = ReferencePattern().Replace(value, match =>
            Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? match.Value);
        return expanded.Replace("$${", "${");
    }

    /// <summary>
    /// JSON-aware expansion: rebuilds the tree expanding every string leaf, so a
    /// secret containing quotes or backslashes can never corrupt the document
    /// (plain text substitution into raw JSON could).
    /// </summary>
    public static JsonNode? ExpandNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (key, child) in obj) result[key] = ExpandNode(child);
                return result;
            }
            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (var child in array) result.Add(ExpandNode(child));
                return result;
            }
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(Expand(text));
            case null:
                return null;
            default:
                return node.DeepClone();
        }
    }
}
