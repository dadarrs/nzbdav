namespace NzbWebDAV.Api.Controllers.ClearReadSessions;

public class ClearReadSessionsResponse : BaseApiResponse
{
    public int DeletedCount { get; set; }
}
