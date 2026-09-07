using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.GetReadSessions;

[ApiController]
[Route("api/get-read-sessions")]
public class GetReadSessionsController(ConfigManager configManager) : BaseApiController
{
    private const int DefaultPageSize = 10;
    private const int MaxPageSize = 50;

    private async Task<GetReadSessionsResponse> GetReadSessionsAsync()
    {
        var page = int.TryParse(HttpContext.Request.Query["page"], out var p) ? Math.Max(1, p) : 1;
        var pageSize = int.TryParse(HttpContext.Request.Query["pageSize"], out var s)
            ? Math.Clamp(s, 1, MaxPageSize)
            : DefaultPageSize;
        var search = HttpContext.Request.Query["search"].ToString().Trim();
        var ct = HttpContext.RequestAborted;

        // "Clear history" hides entries behind a watermark rather than deleting them,
        // so dashboard statistics computed from ReadSessions stay intact.
        var clearedBefore = configManager.GetStreamHistoryClearedBefore();

        await using var metrics = new MetricsDbContext();
        var query = metrics.ReadSessions
            .AsNoTracking()
            .Where(x => x.EndedAt > clearedBefore);
        if (search.Length > 0)
        {
            // Match literal substrings, including filenames containing LIKE wildcards.
            var pattern = "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(x => EF.Functions.Like(x.Path, pattern, "\\"));
        }

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        page = Math.Min(page, Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize)));
        var sessions = await query
            .OrderByDescending(x => x.EndedAt)
            .ThenByDescending(x => x.Id)
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
            .ToListAsync(ct).ConfigureAwait(false);

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
