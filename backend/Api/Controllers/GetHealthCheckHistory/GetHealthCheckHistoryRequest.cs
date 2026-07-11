using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckHistory;

public class GetHealthCheckHistoryRequest
{
    public int PageSize { get; init; } = 20;
    public CancellationToken CancellationToken { get; init; }

    public GetHealthCheckHistoryRequest(HttpContext context)
    {
        var pageSizeParam = context.GetQueryParam("pageSize");
        CancellationToken = context.RequestAborted;

        if (pageSizeParam is not null)
        {
            var isValidStartParam = int.TryParse(pageSizeParam, out int pageSize);
            if (!isValidStartParam) throw new BadHttpRequestException("Invalid pageSize parameter");
            PageSize = pageSize;
        }

        var pageParam = context.GetQueryParam("page");
        if (pageParam is not null)
        {
            var isValidPageParam = int.TryParse(pageParam, out int page);
            if (!isValidPageParam || page < 1) throw new BadHttpRequestException("Invalid page parameter");
            Page = page;
        }

        var searchParam = context.GetQueryParam("search");
        if (!string.IsNullOrWhiteSpace(searchParam))
            Search = searchParam.Trim();

        var repairStatusParam = context.GetQueryParam("repairStatus");
        if (repairStatusParam is not null)
        {
            if (!int.TryParse(repairStatusParam, out var repairStatus))
                throw new BadHttpRequestException("Invalid repairStatus parameter");
            RepairStatus = repairStatus;
        }

        var windowDaysParam = context.GetQueryParam("windowDays");
        if (windowDaysParam is not null)
        {
            if (!int.TryParse(windowDaysParam, out var windowDays) || windowDays < 1)
                throw new BadHttpRequestException("Invalid windowDays parameter");
            WindowDays = windowDays;
        }
    }

    public int Page { get; init; } = 1;
    public string? Search { get; init; }

    // filter to a specific repair action (e.g. Repaired/Deleted browse sections)
    public int? RepairStatus { get; init; }

    // limit results to the last N days, so filtered counts line up with the
    // 30-day stat cards that open those sections
    public int? WindowDays { get; init; }
}