using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers.TestUsenetConnection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using UsenetSharp.Models;

namespace NzbWebDAV.Api.Controllers.TestUsenetPipelining;

[ApiController]
[Route("api/test-usenet-pipelining")]
public class TestUsenetPipeliningController() : BaseApiController
{
    // A small probe is enough to tell whether the server tolerates pipelined STAT. We send a
    // handful of commands at once; servers that don't support pipelining stall, drop the
    // connection, or desync rather than answering all of them in order.
    private const int ProbeBatchSize = 5;

    // Well-known groups used to harvest message-ids of articles the server itself carries.
    // Test groups are near-universally present and constantly posted to.
    private static readonly string[] ProbeGroups = ["alt.test", "misc.test", "alt.binaries.test"];

    // How many recent article numbers to try per group while harvesting.
    private const int MaxHarvestAttemptsPerGroup = 15;

    private async Task<TestUsenetPipeliningResponse> TestPipelining(TestUsenetConnectionRequest request)
    {
        BaseNntpClient connection;
        try
        {
            connection = (BaseNntpClient)await UsenetStreamingClient
                .CreateNewConnection(request.ToConnectionDetails(), HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (CouldNotConnectToUsenetException)
        {
            return new TestUsenetPipeliningResponse { Status = true, Connected = false, Supported = false };
        }
        catch (CouldNotLoginToUsenetException)
        {
            return new TestUsenetPipeliningResponse { Status = true, Connected = false, Supported = false };
        }

        try
        {
            // Phase 1 -- transport check. STAT a batch of randomly-generated, non-existent
            // message-ids. These have no side effects and every server answers each with a 430.
            // If we get the expected number of 430s back, the server accepted the whole pipelined
            // batch and answered in order.
            var probeIds = GenerateProbeMessageIds(ProbeBatchSize);
            var results = await connection
                .StatPipelinedAsync(probeIds, HttpContext.RequestAborted)
                .ConfigureAwait(false);

            var transportOk = results.Count == probeIds.Count
                              && results.All(r => r.ResponseType == UsenetResponseType.NoArticleWithThatMessageId);
            if (!transportOk)
                return new TestUsenetPipeliningResponse { Status = true, Connected = true, Supported = false };

            // Phase 2 -- semantic check. A server can pass phase 1 while being useless for health
            // checks: some backends (e.g. UsenetExpress) answer 430 to every STAT-by-message-id,
            // even for articles they carry, which is indistinguishable from phase 1's expected
            // output. So harvest message-ids of articles the server itself claims to have (via
            // GROUP + STAT-by-number, whose 223 echoes each article's message-id) and require a
            // pipelined STAT over those ids to find at least one.
            var existingIds = await HarvestExistingMessageIds(connection, ProbeBatchSize, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (existingIds.Count == 0)
                return new TestUsenetPipeliningResponse { Status = true, Connected = true, Supported = false };

            var existingResults = await connection
                .StatPipelinedAsync(existingIds, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            var semanticsOk = existingResults.Any(r => r.ResponseType == UsenetResponseType.ArticleExists);

            return new TestUsenetPipeliningResponse { Status = true, Connected = true, Supported = semanticsOk };
        }
        catch (Exception e) when (e is not OperationCanceledException ||
                                  !HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Timeout, dropped connection or protocol desync -> not safe to pipeline this provider.
            return new TestUsenetPipeliningResponse { Status = true, Connected = true, Supported = false };
        }
        finally
        {
            connection.Dispose();
        }
    }

    private static async Task<List<string>> HarvestExistingMessageIds
    (
        BaseNntpClient connection,
        int count,
        CancellationToken ct
    )
    {
        var ids = new List<string>(count);
        foreach (var group in ProbeGroups)
        {
            var groupResponse = await connection.GroupAsync(group, ct).ConfigureAwait(false);
            if (!groupResponse.GroupExists || groupResponse.HighWaterMark <= 0) continue;

            var high = groupResponse.HighWaterMark;
            var low = Math.Max(groupResponse.LowWaterMark, high - MaxHarvestAttemptsPerGroup + 1);
            for (var number = high; number >= low && ids.Count < count; number--)
            {
                var stat = await connection.StatByNumberAsync(number, ct).ConfigureAwait(false);
                if (!stat.ArticleExists) continue;
                var messageId = ExtractMessageId(stat.ResponseMessage);
                if (messageId is not null) ids.Add(messageId);
            }

            if (ids.Count >= count) break;
        }

        return ids;
    }

    private static string? ExtractMessageId(string responseLine)
    {
        // "223 number <message-id>" -- return the id without the angle brackets.
        var start = responseLine.IndexOf('<');
        var end = responseLine.LastIndexOf('>');
        return start >= 0 && end > start + 1
            ? responseLine.Substring(start + 1, end - start - 1)
            : null;
    }

    private static List<string> GenerateProbeMessageIds(int count)
    {
        var ids = new List<string>(count);
        for (var i = 0; i < count; i++)
            ids.Add($"nzbdav-pipelining-probe-{Guid.NewGuid():N}@nzbdav.invalid");
        return ids;
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestUsenetConnectionRequest(HttpContext);
        var response = await TestPipelining(request).ConfigureAwait(false);
        return Ok(response);
    }
}
