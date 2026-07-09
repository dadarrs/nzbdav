namespace NzbWebDAV.Api.Controllers.TriggerHealthCheck;

public class TriggerHealthCheckResponse : BaseApiResponse
{
    public int TriggeredCount { get; set; }
    public bool RepairJobEnabled { get; set; }
}
