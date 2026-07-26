using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class ArrClient(string host, string apiKey)
{
    protected static readonly HttpClient HttpClient = new();

    public string Host { get; } = host;
    private string ApiKey { get; } = apiKey;
    private const string BasePath = "/api/v3";

    public Task<ArrApiInfoResponse> GetApiInfo() =>
        GetRoot<ArrApiInfoResponse>($"/api");

    public virtual Task<ArrRepairedMedia?> RemoveAndSearch(string symlinkOrStrmPath) =>
        throw new InvalidOperationException();

    /// <summary>
    /// Resolves which library item a path belongs to WITHOUT modifying the arr.
    /// Unlike RemoveAndSearch's file lookup, this matches by item folder, so it
    /// still works when the arr has no file for the item (e.g. a deletion where
    /// no replacement could be grabbed). Used to deep-link deleted rows on the
    /// health page.
    /// </summary>
    public virtual Task<ArrRepairedMedia?> TryIdentify(string symlinkOrStrmPath) =>
        Task.FromResult<ArrRepairedMedia?>(null);

    public Task<List<ArrRootFolder>> GetRootFolders() =>
        Get<List<ArrRootFolder>>($"/rootfolder");

    public Task<List<ArrDownloadClient>> GetDownloadClientsAsync() =>
        Get<List<ArrDownloadClient>>($"/downloadClient");

    public Task<ArrCommand> RefreshMonitoredDownloads() =>
        CommandAsync(new { name = "RefreshMonitoredDownloads" });

    public Task<ArrQueueStatus> GetQueueStatusAsync() =>
        Get<ArrQueueStatus>($"/queue/status");

    public Task<ArrQueue<ArrQueueRecord>> GetQueueAsync() =>
        Get<ArrQueue<ArrQueueRecord>>($"/queue?protocol=usenet&pageSize=5000");

    // Grabbed-history events (eventType=1), newest first. Carries downloadId -> indexer
    // and, unlike the queue, persists after the download completes — so it's the reliable
    // source for resolving an import's indexer. Paged; the caller stops at its lookback.
    public Task<ArrHistoryResponse> GetGrabbedHistoryAsync(int page, int pageSize) =>
        Get<ArrHistoryResponse>(
            $"/history?eventType=1&page={page}&pageSize={pageSize}&sortKey=date&sortDirection=descending");

    public async Task<int> GetQueueCountAsync() =>
        (await Get<ArrQueue<ArrQueueRecord>>($"/queue?pageSize=1")).TotalRecords;

    public Task<HttpStatusCode> DeleteQueueRecord(int id, DeleteQueueRecordRequest request) =>
        Delete($"/queue/{id}", request.GetQueryParams());

    public Task<HttpStatusCode> DeleteQueueRecord(int id, ArrConfig.QueueAction request) =>
        request is not ArrConfig.QueueAction.DoNothing
            ? Delete($"/queue/{id}", new DeleteQueueRecordRequest(request).GetQueryParams())
            : Task.FromResult(HttpStatusCode.OK);

    public Task<ArrCommand> CommandAsync(object command) =>
        Post<ArrCommand>($"/command", command);

    protected Task<T> Get<T>(string path) =>
        GetRoot<T>($"{BasePath}{path}");

    protected async Task<T> GetRoot<T>(string rootPath)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Host}{rootPath}");
        using var response = await SendAsync(request);
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<T>(stream) ?? throw new NullReferenceException();
    }

    protected async Task<T> Post<T>(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GetRequestUri(path));
        var jsonBody = JsonSerializer.Serialize(body);
        request.Content = new StringContent(jsonBody, new MediaTypeHeaderValue("application/json"));
        using var response = await SendAsync(request);
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<T>(stream) ?? throw new NullReferenceException();
    }

    protected async Task<HttpStatusCode> Delete(string path, Dictionary<string, string>? queryParams = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, GetRequestUri(path, queryParams));
        using var response = await SendAsync(request);
        return response.StatusCode;
    }

    private string GetRequestUri(string path, Dictionary<string, string>? queryParams = null)
    {
        queryParams ??= new Dictionary<string, string>();
        var resource = $"{Host}{BasePath}{path}";
        var query = queryParams.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
        var queryString = string.Join("&", query);
        if (queryString.Length > 0) resource = $"{resource}?{queryString}";
        return resource;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        request.Headers.Add("X-Api-Key", ApiKey);
        return HttpClient.SendAsync(request);
    }
}