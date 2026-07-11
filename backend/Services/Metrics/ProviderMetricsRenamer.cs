using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Keeps metrics attribution continuous across provider renames. Metric rows are
/// keyed by the effective name (nickname or deduped host) that was current when
/// they were written, so changing a nickname would otherwise strand the account's
/// history under the old name and show two providers on the dashboard. When a
/// settings save changes an account's effective name, the old name's rows are
/// migrated to the new name before the streaming client rebuilds and reseeds.
/// </summary>
public static class ProviderMetricsRenamer
{
    /// <summary>
    /// Pairs accounts between the old and new provider lists by (Host, User) in
    /// list order -- the stable identity a nickname edit doesn't touch -- and
    /// returns the effective-name changes. Accounts that were added or removed
    /// have no pair and are skipped; removed names simply age out via retention.
    /// </summary>
    public static List<(string OldName, string NewName)> ComputeRenames(
        UsenetProviderConfig oldConfig, UsenetProviderConfig newConfig)
    {
        var oldNames = oldConfig.GetEffectiveNames();
        var newNames = newConfig.GetEffectiveNames();
        var renames = new List<(string, string)>();
        var claimed = new bool[newConfig.Providers.Count];
        for (var i = 0; i < oldConfig.Providers.Count; i++)
        {
            var oldProvider = oldConfig.Providers[i];
            var match = -1;
            for (var j = 0; j < newConfig.Providers.Count; j++)
            {
                if (claimed[j]) continue;
                var candidate = newConfig.Providers[j];
                if (string.Equals(candidate.Host, oldProvider.Host, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.User, oldProvider.User, StringComparison.Ordinal))
                {
                    match = j;
                    break;
                }
            }

            if (match < 0) continue;
            claimed[match] = true;
            if (!string.Equals(oldNames[i], newNames[match], StringComparison.Ordinal))
                renames.Add((oldNames[i], newNames[match]));
        }

        return renames;
    }

    /// <summary>
    /// Migrates every metric row from oldName to newName. The rollup tables key on
    /// (bucket, provider), so a plain UPDATE would collide when the new name already
    /// has rows for the same bucket; those are merged additively instead (histogram
    /// and P95 keep the existing row's value -- a same-bucket collision only happens
    /// when renaming into a name that was live in the same minute/hour).
    /// </summary>
    public static async Task RenameAsync(string oldName, string newName, CancellationToken ct = default)
    {
        await using var db = new MetricsDbContext();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ProviderMinutes
                (Minute, Provider, Articles, BytesFetched, Errors, Retries, FailoverSaves,
                 SumDurationMs, Hist, HealthBytesOnAdd, HealthBytesBackground)
            SELECT Minute, {newName}, Articles, BytesFetched, Errors, Retries, FailoverSaves,
                   SumDurationMs, Hist, HealthBytesOnAdd, HealthBytesBackground
            FROM ProviderMinutes WHERE Provider = {oldName}
            ON CONFLICT(Minute, Provider) DO UPDATE SET
                Articles = ProviderMinutes.Articles + excluded.Articles,
                BytesFetched = ProviderMinutes.BytesFetched + excluded.BytesFetched,
                Errors = ProviderMinutes.Errors + excluded.Errors,
                Retries = ProviderMinutes.Retries + excluded.Retries,
                FailoverSaves = ProviderMinutes.FailoverSaves + excluded.FailoverSaves,
                SumDurationMs = ProviderMinutes.SumDurationMs + excluded.SumDurationMs,
                HealthBytesOnAdd = ProviderMinutes.HealthBytesOnAdd + excluded.HealthBytesOnAdd,
                HealthBytesBackground = ProviderMinutes.HealthBytesBackground + excluded.HealthBytesBackground
            """, ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM ProviderMinutes WHERE Provider = {oldName}", ct).ConfigureAwait(false);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ProviderHourly
                (Hour, Provider, Articles, BytesFetched, Errors, Retries, FailoverSaves,
                 SumDurationMs, P95DurationMs, HealthBytesOnAdd, HealthBytesBackground)
            SELECT Hour, {newName}, Articles, BytesFetched, Errors, Retries, FailoverSaves,
                   SumDurationMs, P95DurationMs, HealthBytesOnAdd, HealthBytesBackground
            FROM ProviderHourly WHERE Provider = {oldName}
            ON CONFLICT(Hour, Provider) DO UPDATE SET
                Articles = ProviderHourly.Articles + excluded.Articles,
                BytesFetched = ProviderHourly.BytesFetched + excluded.BytesFetched,
                Errors = ProviderHourly.Errors + excluded.Errors,
                Retries = ProviderHourly.Retries + excluded.Retries,
                FailoverSaves = ProviderHourly.FailoverSaves + excluded.FailoverSaves,
                SumDurationMs = ProviderHourly.SumDurationMs + excluded.SumDurationMs,
                HealthBytesOnAdd = ProviderHourly.HealthBytesOnAdd + excluded.HealthBytesOnAdd,
                HealthBytesBackground = ProviderHourly.HealthBytesBackground + excluded.HealthBytesBackground
            """, ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM ProviderHourly WHERE Provider = {oldName}", ct).ConfigureAwait(false);

        await db.Database.ExecuteSqlAsync(
            $"UPDATE SegmentFetches SET Provider = {newName} WHERE Provider = {oldName}", ct)
            .ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE FailoverMisses SET FromProvider = {newName} WHERE FromProvider = {oldName}", ct)
            .ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE FailoverMisses SET ToProvider = {newName} WHERE ToProvider = {oldName}", ct)
            .ConfigureAwait(false);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO FailoverHourly (Hour, FromProvider, ToProvider, Reason, Count)
            SELECT Hour, {newName}, ToProvider, Reason, Count
            FROM FailoverHourly WHERE FromProvider = {oldName}
            ON CONFLICT(Hour, FromProvider, ToProvider, Reason) DO UPDATE SET
                Count = FailoverHourly.Count + excluded.Count
            """, ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM FailoverHourly WHERE FromProvider = {oldName}", ct).ConfigureAwait(false);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO FailoverHourly (Hour, FromProvider, ToProvider, Reason, Count)
            SELECT Hour, FromProvider, {newName}, Reason, Count
            FROM FailoverHourly WHERE ToProvider = {oldName}
            ON CONFLICT(Hour, FromProvider, ToProvider, Reason) DO UPDATE SET
                Count = FailoverHourly.Count + excluded.Count
            """, ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM FailoverHourly WHERE ToProvider = {oldName}", ct).ConfigureAwait(false);

        Log.Information("Migrated provider metrics from \"{OldName}\" to \"{NewName}\"", oldName, newName);
    }
}
