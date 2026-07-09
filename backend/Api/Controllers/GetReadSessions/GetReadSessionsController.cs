using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.GetReadSessions;

[ApiController]
[Route("api/get-read-sessions")]
public class GetReadSessionsController : BaseApiController
{
    private const int DefaultPageSize = 10;
    private const int MaxPageSize = 50;

    private async Task<GetReadSessionsResponse> GetReadSessionsAsync()
    {
        var page = int.TryParse(HttpContext.Request.Query["page"], out var p) ? Math.Max(1, p) : 1;
        var pageSize = int.TryParse(HttpContext.Request.Query["pageSize"], out var s)
            ? Math.Clamp(s, 1, MaxPageSize)
            : DefaultPageSize;

        await using var metrics = new MetricsDbContext();
        var query = metrics.ReadSessions.OrderByDescending(x => x.EndedAt);
        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var sessions = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new GetReadSessionsResponse.ReadSessionItem
            {
                Id = x.Id,
                Path = x.Path,
                StartedAt = x.StartedAt,
                EndedAt = x.EndedAt,
                DurationMs = x.DurationMs,
                FileSize = x.FileSize,
                BytesServed = x.BytesServed,
                FailoverSaves = x.FailoverSaves,
                ClientIp = x.ClientIp,
                ClientUserAgent = x.ClientUserAgent,
                EndReason = (int)x.EndReason,
            })
            .ToListAsync().ConfigureAwait(false);

        return new GetReadSessionsResponse
        {
            Status = true,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            Sessions = sessions,
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var response = await GetReadSessionsAsync().ConfigureAwait(false);
        return Ok(response);
    }
}
