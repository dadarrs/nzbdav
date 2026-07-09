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
    }

    public int Page { get; init; } = 1;
    public string? Search { get; init; }
}