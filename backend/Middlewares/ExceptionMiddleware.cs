using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using NWebDav.Server.Helpers;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Middlewares;

public class ExceptionMiddleware(RequestDelegate next, ConfigManager configManager)
{
    // When a stream hits a missing article, schedule the item for an immediate background
    // health check (which repairs via arr re-search). Deduped per item so a burst of reads
    // against the same dead file only triggers one repair.
    private static readonly ConcurrentDictionary<Guid, DateTime> _recentRepairTriggers = new();
    private static readonly TimeSpan RepairDedupeWindow = TimeSpan.FromMinutes(5);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception e) when (IsCausedByAbortedRequest(e, context))
        {
            // If the response has not started, we can write our custom response
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 499; // Non-standard status code for client closed request
                await context.Response.WriteAsync("Client closed request.").ConfigureAwait(false);
            }
        }
        catch (UsenetArticleNotFoundException e)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 404;
            }

            var filePath = GetRequestFilePath(context);
            Log.Error($"File `{filePath}` has missing articles: {e.Message}");

            if (context.Items["DavItem"] is DavItem davItem)
                ScheduleRepair(davItem.Id);
        }
        catch (SeekPositionNotFoundException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 404;
            }

            var filePath = GetRequestFilePath(context);
            var seekPosition = context.Request.GetRange()?.Start?.ToString() ?? "unknown";
            Log.Error($"File `{filePath}` could not seek to byte position: {seekPosition}");
        }
        catch (Exception e) when (IsDavItemRequest(context))
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 500;
            }

            var filePath = GetRequestFilePath(context);
            var seekPosition = context.Request.GetRange()?.Start?.ToString() ?? "0";
            Log.Error($"File `{filePath}` could not be read from byte position: {seekPosition} " +
                      $"due to unhandled {e.GetType()}: {e.Message}");
        }
    }

    private bool IsCausedByAbortedRequest(Exception e, HttpContext context)
    {
        var isAffectedException = e is OperationCanceledException or EndOfStreamException;
        var isRequestAborted = context.RequestAborted.IsCancellationRequested ||
                               SigtermUtil.GetCancellationToken().IsCancellationRequested;
        return isAffectedException && isRequestAborted;
    }

    private void ScheduleRepair(Guid davItemId)
    {
        if (!configManager.IsRepairJobEnabled())
            return;

        var now = DateTime.UtcNow;
        var isDuplicate = false;
        _recentRepairTriggers.AddOrUpdate(
            davItemId,
            _ => now,
            (_, existing) =>
            {
                if (now - existing < RepairDedupeWindow)
                {
                    isDuplicate = true;
                    return existing;
                }

                return now;
            }
        );

        if (isDuplicate)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var dbContext = new DavDatabaseContext();
                var item = await dbContext.Items.FindAsync(davItemId).ConfigureAwait(false);
                if (item == null)
                    return;

                // Use UnixEpoch as a sentinel so dynamic repairs sort first in the
                // HealthCheckService queue (which orders non-NULL before NULL, then by
                // NextHealthCheck ascending). This beats any real timestamp.
                var urgent = DateTimeOffset.UnixEpoch;
                if (item.NextHealthCheck == urgent)
                    return;

                item.NextHealthCheck = urgent;
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
                Log.Information("Scheduled dynamic repair for {FilePath}", item.Path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to schedule dynamic repair for DavItem {DavItemId}", davItemId);
            }
        });
    }

    private static string GetRequestFilePath(HttpContext context)
    {
        return context.Items["DavItem"] is DavItem davItem
            ? davItem.Path
            : context.Request.Path;
    }

    private static bool IsDavItemRequest(HttpContext context)
    {
        return context.Items["DavItem"] is DavItem;
    }
}