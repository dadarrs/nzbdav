using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.RemoveFromHistory;

public class RemoveFromHistoryController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<RemoveFromHistoryResponse> RemoveFromHistory(RemoveFromHistoryRequest request)
    {
        if (request.DeleteAll)
        {
            await dbClient.RemoveAllHistoryItemsAsync(request.DeleteCompletedFiles, request.CancellationToken).ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
            // "*" tells the frontend to drop its entire cached history view.
            _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, "*");
            return new RemoveFromHistoryResponse() { Status = true };
        }

        await dbClient.RemoveHistoryItemsAsync(request.NzoIds, request.DeleteCompletedFiles, request.CancellationToken).ConfigureAwait(false);
        await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
        _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, string.Join(",", request.NzoIds));
        return new RemoveFromHistoryResponse() { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await RemoveFromHistoryRequest.New(httpContext).ConfigureAwait(false);
        return Ok(await RemoveFromHistory(request).ConfigureAwait(false));
    }
}