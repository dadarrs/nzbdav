using System.Net;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class RadarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    private static readonly Dictionary<string, int> SymlinkOrStrmToMovieIdCache = new();

    public Task<RadarrMovie> GetMovieAsync(int id) =>
        Get<RadarrMovie>($"/movie/{id}");

    public Task<List<RadarrMovie>> GetMoviesAsync() =>
        Get<List<RadarrMovie>>($"/movie");

    public Task<RadarrQueue> GetRadarrQueueAsync() =>
        Get<RadarrQueue>($"/queue?protocol=usenet&pageSize=5000");

    public Task<HttpStatusCode> DeleteMovieFile(int id) =>
        Delete($"/moviefile/{id}");

    public Task<ArrCommand> SearchMovieAsync(int id) =>
        CommandAsync(new { name = "MoviesSearch", movieIds = new List<int> { id } });


    public override async Task<ArrRepairedMedia?> RemoveAndSearch(string symlinkOrStrmPath)
    {
        var media = await GetMedia(symlinkOrStrmPath);
        if (media == null) return null;

        if (await DeleteMovieFile(media.Value.movieFileId) != HttpStatusCode.OK)
            throw new Exception($"Failed to delete movie file `{symlinkOrStrmPath}` from radarr instance `{Host}`.");

        await SearchMovieAsync(media.Value.movie.Id);
        return new ArrRepairedMedia
        {
            Kind = ArrRepairedMedia.RadarrKind,
            ItemId = media.Value.movie.Id,
            TitleSlug = media.Value.movie.TitleSlug,
            Title = media.Value.movie.Title,
        };
    }

    public override async Task<ArrRepairedMedia?> TryIdentify(string symlinkOrStrmPath)
    {
        // prefer the exact movie-file match when radarr still has the file
        var media = await GetMedia(symlinkOrStrmPath);
        var movie = media?.movie;

        // otherwise match by movie folder, which survives the file's deletion
        if (movie == null)
        {
            foreach (var candidate in await GetMoviesAsync())
            {
                var folder = candidate.Path?.TrimEnd('/');
                if (folder != null && symlinkOrStrmPath.StartsWith($"{folder}/"))
                {
                    movie = candidate;
                    break;
                }
            }
        }

        if (movie == null) return null;
        return new ArrRepairedMedia
        {
            Kind = ArrRepairedMedia.RadarrKind,
            ItemId = movie.Id,
            TitleSlug = movie.TitleSlug,
            Title = movie.Title,
        };
    }

    private async Task<(int movieFileId, RadarrMovie movie)?> GetMedia(string symlinkOrStrmPath)
    {
        // if we already have the movie-id cached
        // then let's use it to find and return the corresponding movie-file-id
        if (SymlinkOrStrmToMovieIdCache.TryGetValue(symlinkOrStrmPath, out var movieId))
        {
            var movie = await GetMovieAsync(movieId);
            if (movie.MovieFile?.Path == symlinkOrStrmPath)
                return (movie.MovieFile.Id!, movie);
        }

        // otherwise, let's fetch all movies, cache all movie files
        // and return the matching movie and movie-file-id
        var allMovies = await GetMoviesAsync();
        (int movieFileId, RadarrMovie movie)? result = null;
        foreach (var movie in allMovies)
        {
            var movieFile = movie.MovieFile;
            if (movieFile?.Path != null)
                SymlinkOrStrmToMovieIdCache[movieFile.Path] = movie.Id;
            if (movieFile?.Path == symlinkOrStrmPath)
                result = (movieFile.Id!, movie);
        }

        return result;
    }
}