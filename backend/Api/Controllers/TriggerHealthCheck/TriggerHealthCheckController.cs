using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Api.Controllers.TriggerHealthCheck;

[ApiController]
[Route("api/trigger-health-check")]
public class TriggerHealthCheckController(DavDatabaseClient dbClient, ConfigManager configManager)
    : BaseApiController
{
    private async Task<TriggerHealthCheckResponse> TriggerAsync()
    {
        var idsParam = HttpContext.Request.Query["ids"].FirstOrDefault();
        if (idsParam is null && HttpContext.Request.HasFormContentType)
            idsParam = (await HttpContext.Request.ReadFormAsync().ConfigureAwait(false))["ids"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(idsParam))
            throw new BadHttpRequestException("Missing ids parameter");

        var ids = idsParam
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Guid.TryParse(x, out var id) ? id : (Guid?)null)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            throw new BadHttpRequestException("No valid ids provided");

        // UnixEpoch sentinel sorts first in the health-check queue (non-NULL before NULL,
        // then ascending), so triggered items get picked up on the service's next tick --
        // same mechanism the dynamic repair trigger uses.
        var urgent = DateTimeOffset.UnixEpoch;
        var items = await dbClient.Ctx.Items
            .Where(x => ids.Contains(x.Id))
            .ToListAsync().ConfigureAwait(false);
        foreach (var item in items)
            item.NextHealthCheck = urgent;
        await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);

        Log.Information("Health check triggered manually for {Count} item(s)", items.Count);
        return new TriggerHealthCheckResponse
        {
            Status = true,
            TriggeredCount = items.Count,
            // the background service only runs when the repair job is enabled; surface
            // that so the UI can warn instead of silently queueing forever
            RepairJobEnabled = configManager.IsRepairJobEnabled(),
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var response = await TriggerAsync().ConfigureAwait(false);
        return Ok(response);
    }
}
