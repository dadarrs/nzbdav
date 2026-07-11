using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Api.Controllers.GetProviderUsage;

[ApiController]
[Route("api/get-provider-usage")]
public class GetProviderUsageController(
    ConfigManager configManager,
    ProviderBytesTracker bytesTracker
) : BaseApiController
{
    private async Task<GetProviderUsageResponse> GetUsageAsync()
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        // metrics are keyed per-account by effective name (nickname or deduped host)
        var effectiveNames = providerConfig.GetEffectiveNames();
        var recentHoursByName = await ProviderUsageHelper
            .ReadRecentHoursAsync(effectiveNames)
            .ConfigureAwait(false);

        var items = providerConfig.Providers
            .Select((provider, index) =>
            {
                var name = effectiveNames[index];
                var used = ProviderUsageHelper.ComputeUsage(bytesTracker, provider, name);
                recentHoursByName.TryGetValue(name, out var recentHours);
                var (bytesPerDay, daysRemaining) = ProviderUsageHelper.ComputeBurnRate(provider, used, recentHours);
                return new GetProviderUsageResponse.ProviderUsageItem
                {
                    Index = index,
                    Host = provider.Host,
                    Nickname = name,
                    BytesUsed = used,
                    ByteLimit = provider.ByteLimit,
                    OverLimit = ProviderUsageHelper.IsOverLimit(bytesTracker, provider, name),
                    BytesPerDay = bytesPerDay,
                    DaysRemaining = daysRemaining,
                };
            })
            .ToList();

        return new GetProviderUsageResponse
        {
            Status = true,
            Providers = items,
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var response = await GetUsageAsync().ConfigureAwait(false);
        return Ok(response);
    }
}
