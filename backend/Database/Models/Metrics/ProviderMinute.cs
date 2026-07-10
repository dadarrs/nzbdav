namespace NzbWebDAV.Database.Models.Metrics;

public class ProviderMinute
{
    public long Minute { get; set; }
    public string Provider { get; set; } = null!;
    public long Articles { get; set; }
    public long BytesFetched { get; set; }
    public long Errors { get; set; }
    public long Retries { get; set; }
    public long FailoverSaves { get; set; }
    public long SumDurationMs { get; set; }
    public byte[]? Hist { get; set; }

    // STAT health-check protocol bytes (command + response lines), split by
    // whether the check ran on-add (import) or from the background repair job.
    public long HealthBytesOnAdd { get; set; }
    public long HealthBytesBackground { get; set; }
}
