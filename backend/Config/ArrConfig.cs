using NzbWebDAV.Clients.RadarrSonarr;

namespace NzbWebDAV.Config;

public class ArrConfig
{
    public List<ConnectionDetails> RadarrInstances { get; set; } = [];
    public List<ConnectionDetails> SonarrInstances { get; set; } = [];
    public List<QueueRule> QueueRules { get; set; } = [];

    // ReSharper disable once InvokeAsExtensionMethod
    public IEnumerable<ArrClient> GetArrClients() => Enumerable.Concat(
        RadarrInstances.Select(ArrClient (x) => new RadarrClient(x.Host, x.ApiKey)),
        SonarrInstances.Select(ArrClient (x) => new SonarrClient(x.Host, x.ApiKey))
    );

    public int GetInstanceCount() =>
        RadarrInstances.Count + SonarrInstances.Count;

    public class ConnectionDetails
    {
        public required string Host { get; set; }
        public required string ApiKey { get; set; }

        // Optional externally-reachable URL for this instance (e.g. a reverse-proxy
        // hostname). Used only to build clickable links in the UI; API calls always
        // go through Host. Optional so configs saved before this existed still load.
        public string? PublicUrl { get; set; }
    }

    public class QueueRule
    {
        public string Message { get; set; } = null!;
        public QueueAction Action { get; set; }
    }

    public enum QueueAction
    {
        DoNothing = 0,
        Remove = 1,
        RemoveAndBlocklist = 2,
        RemoveAndBlocklistAndSearch = 3
    }
}