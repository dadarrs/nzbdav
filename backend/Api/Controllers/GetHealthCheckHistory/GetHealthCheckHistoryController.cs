using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckHistory;

[ApiController]
[Route("api/get-health-check-history")]
public class GetHealthCheckHistoryController(DavDatabaseClient dbClient) : BaseApiController
{
    private async Task<GetHealthCheckHistoryResponse> GetHealthCheckHistory(GetHealthCheckHistoryRequest request)
    {
        var now = DateTime.UtcNow;
        var tomorrow = now.AddDays(1);
        var thirtyDaysAgo = now.AddDays(-30);
        var statsPromise = dbClient.GetHealthCheckStatsAsync(thirtyDaysAgo, tomorrow);

        var query = dbClient.Ctx.HealthCheckResults.AsQueryable();
        if (request.Search is not null)
        {
            foreach (var token in request.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = token.ToLower();
                query = query.Where(x => x.Path.ToLower().Contains(t));
            }
        }

        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var itemsPromise = query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new GetHealthCheckHistoryResponse()
        {
            Stats = await statsPromise.ConfigureAwait(false),
            Items = await itemsPromise.ConfigureAwait(false),
            TotalCount = totalCount,
            Page = request.Page,
            PageSize = request.PageSize,
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetHealthCheckHistoryRequest(HttpContext);
        var response = await GetHealthCheckHistory(request).ConfigureAwait(false);
        return Ok(response);
    }
}