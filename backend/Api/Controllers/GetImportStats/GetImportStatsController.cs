using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.GetImportStats;

[ApiController]
[Route("api/get-import-stats")]
public class GetImportStatsController : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var idParam = HttpContext.Request.Query["id"].ToString();
        if (!Guid.TryParse(idParam, out var id))
            throw new BadHttpRequestException("Invalid id parameter");

        await using var metrics = new MetricsDbContext();
        var stat = await metrics.ImportStats
            .FirstOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (stat == null)
            return Ok(new GetImportStatsResponse { Status = true, Found = false });

        List<GetImportStatsResponse.ProviderStat> providers;
        try
        {
            providers = JsonSerializer
                .Deserialize<List<GetImportStatsResponse.ProviderStat>>(stat.ProviderBytesJson) ?? [];
        }
        catch (JsonException)
        {
            providers = [];
        }

        return Ok(new GetImportStatsResponse
        {
            Status = true,
            Found = true,
            JobName = stat.JobName,
            CompletedAt = stat.CompletedAt,
            DownloadMs = stat.DownloadMs,
            VerifyMs = stat.VerifyMs,
            TotalMs = stat.TotalMs,
            Failed = stat.Failed,
            Providers = providers,
        });
    }
}
