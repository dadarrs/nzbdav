using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Api.Controllers.ClearReadSessions;

[ApiController]
[Route("api/clear-read-sessions")]
public class ClearReadSessionsController(DavDatabaseClient dbClient, ConfigManager configManager)
    : BaseApiController
{
    private const string ClearedBeforeKey = "metrics.stream-history-cleared-before";

    protected override async Task<IActionResult> HandleRequest()
    {
        // Clearing hides history entries behind a watermark instead of deleting the
        // ReadSession rows: dashboard statistics (lifetime read totals, session counts,
        // throughput served-bytes, failover reads-saved) are computed from those rows
        // and must survive a list clear. Hidden rows still age out via normal retention.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var previousCutoff = configManager.GetStreamHistoryClearedBefore();

        int hiddenCount;
        await using (var metrics = new MetricsDbContext())
        {
            hiddenCount = await metrics.ReadSessions
                .CountAsync(x => x.EndedAt > previousCutoff && x.EndedAt <= nowMs)
                .ConfigureAwait(false);
        }

        var existing = await dbClient.Ctx.ConfigItems
            .FirstOrDefaultAsync(c => c.ConfigName == ClearedBeforeKey, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        if (existing != null) existing.ConfigValue = nowMs.ToString();
        else dbClient.Ctx.ConfigItems.Add(new ConfigItem
        {
            ConfigName = ClearedBeforeKey,
            ConfigValue = nowMs.ToString(),
        });
        await dbClient.Ctx.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        configManager.UpdateValues([
            new ConfigItem { ConfigName = ClearedBeforeKey, ConfigValue = nowMs.ToString() }
        ]);

        Log.Information("Stream history cleared ({Count} entries hidden; metrics rows retained)", hiddenCount);
        return Ok(new ClearReadSessionsResponse { Status = true, DeletedCount = hiddenCount });
    }
}
