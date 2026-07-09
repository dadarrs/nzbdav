using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckQueue;

[ApiController]
[Route("api/get-health-check-queue")]
public class GetHealthCheckQueueController(DavDatabaseClient dbClient) : BaseApiController
{
    private async Task<GetHealthCheckQueueResponse> GetHealthCheckQueue(GetHealthCheckQueueRequest request)
    {
        var query = HealthCheckService.GetHealthCheckQueueItems(dbClient).AsQueryable();

        // fuzzy-ish search: every whitespace-separated token must appear in the name
        if (request.Search is not null)
        {
            foreach (var token in request.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = token.ToLower();
                query = query.Where(x => x.Name.ToLower().Contains(t));
            }
        }

        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var davItems = await query
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync().ConfigureAwait(false);

        var uncheckedCount = await HealthCheckService.GetHealthCheckQueueItemsQuery(dbClient)
            .Where(x => x.NextHealthCheck == null)
            .CountAsync().ConfigureAwait(false);

        return new GetHealthCheckQueueResponse()
        {
            UncheckedCount = uncheckedCount,
            TotalCount = totalCount,
            Page = request.Page,
            PageSize = request.PageSize,
            Items = davItems.Select(x => new GetHealthCheckQueueResponse.HealthCheckQueueItem()
            {
                Id = x.Id.ToString(),
                Name = x.Name,
                Path = x.Path,
                ReleaseDate = x.ReleaseDate,
                LastHealthCheck = x.LastHealthCheck,
                NextHealthCheck = x.NextHealthCheck,
            }).ToList(),
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetHealthCheckQueueRequest(HttpContext);
        var response = await GetHealthCheckQueue(request).ConfigureAwait(false);
        return Ok(response);
    }
}