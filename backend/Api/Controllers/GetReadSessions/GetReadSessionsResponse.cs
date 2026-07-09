namespace NzbWebDAV.Api.Controllers.GetReadSessions;

public class GetReadSessionsResponse : BaseApiResponse
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public long TotalCount { get; set; }
    public List<ReadSessionItem> Sessions { get; set; } = new();

    public class ReadSessionItem
    {
        public Guid Id { get; set; }
        public string Path { get; set; } = string.Empty;
        public long StartedAt { get; set; }
        public long EndedAt { get; set; }
        public int DurationMs { get; set; }
        public long? FileSize { get; set; }
        public long BytesServed { get; set; }
        public int FailoverSaves { get; set; }
        public string? ClientIp { get; set; }
        public string? ClientUserAgent { get; set; }
        // ReadSession.EndReasonCode: 0 Completed, 1 Aborted, 2 Timeout, 3 Error
        public int EndReason { get; set; }
    }
}
