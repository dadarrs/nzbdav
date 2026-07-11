using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

[ApiController]
[Route("api/update-config")]
public class UpdateConfigController(DavDatabaseClient dbClient, ConfigManager configManager) : BaseApiController
{
    private async Task<UpdateConfigResponse> UpdateConfig(UpdateConfigRequest request)
    {
        // 1. Retrieve all ConfigItems from the database that match the ConfigNames in the request
        var configNames = request.ConfigItems.Select(x => x.ConfigName).ToHashSet();

        // Snapshot the current provider config before it is overwritten: if this save
        // renames an account, its metric rows must follow the new effective name.
        var oldProviderConfig = configNames.Contains("usenet.providers")
            ? configManager.GetUsenetProviderConfig()
            : null;
        var existingItems = await dbClient.Ctx.ConfigItems
            .Where(c => configNames.Contains(c.ConfigName))
            .ToListAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        // 2. Split the items into those that need to be updated and those that need to be inserted
        var existingItemsDict = existingItems.ToDictionary(i => i.ConfigName);
        var itemsToUpdate = new List<ConfigItem>();
        var itemsToInsert = new List<ConfigItem>();
        foreach (var item in request.ConfigItems)
        {
            if (existingItemsDict.TryGetValue(item.ConfigName, out ConfigItem? existingItem))
            {
                existingItem.ConfigValue = item.ConfigValue;
                itemsToUpdate.Add(existingItem);
            }
            else
            {
                itemsToInsert.Add(item);
            }
        }

        // 3. Perform bulk insert and bulk update
        dbClient.Ctx.ConfigItems.AddRange(itemsToInsert);
        dbClient.Ctx.ConfigItems.UpdateRange(itemsToUpdate);

        // 4. Save changes in one call
        await dbClient.Ctx.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        // 5. Migrate metric rows for renamed provider accounts. This must happen before
        //    UpdateValues: that call rebuilds the streaming client and reseeds the data-cap
        //    tracker from ProviderHourly, which should already see the merged history.
        if (oldProviderConfig != null)
            await MigrateRenamedProviderMetricsAsync(oldProviderConfig, request).ConfigureAwait(false);

        // 6. Update the ConfigManager
        configManager.UpdateValues(request.ConfigItems);

        // return
        return new UpdateConfigResponse { Status = true };
    }

    private async Task MigrateRenamedProviderMetricsAsync(
        UsenetProviderConfig oldProviderConfig, UpdateConfigRequest request)
    {
        try
        {
            var newJson = StringUtil.EmptyToNull(request.ConfigItems
                .First(x => x.ConfigName == "usenet.providers").ConfigValue);
            var newProviderConfig = newJson == null
                ? new UsenetProviderConfig()
                : JsonSerializer.Deserialize<UsenetProviderConfig>(newJson) ?? new UsenetProviderConfig();
            foreach (var (oldName, newName) in
                     ProviderMetricsRenamer.ComputeRenames(oldProviderConfig, newProviderConfig))
                await ProviderMetricsRenamer.RenameAsync(oldName, newName, HttpContext.RequestAborted)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the config save itself succeeded; a failed metrics migration just leaves
            // history under the old name, which retention ages out eventually
            Log.Warning(ex, "Failed to migrate provider metrics after rename");
        }
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new UpdateConfigRequest(HttpContext);
        var response = await UpdateConfig(request).ConfigureAwait(false);
        return Ok(response);
    }
}