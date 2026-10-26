// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Library.Service.Discover;

/// <summary>
/// Talks to Radarr and Sonarr (their v3 APIs are close enough to share one client): what they already have,
/// adding a title unmonitored so it can be searched, listing the releases indexers offer, and grabbing one.
/// </summary>
public sealed class ArrClient
{
    private readonly HttpClient _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrClient"/> class.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    public ArrClient(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(90);
    }

    /// <summary>
    /// Lists what Radarr holds: TMDB id to Radarr id and whether the file is on disk.
    /// </summary>
    /// <param name="url">Radarr's address.</param>
    /// <param name="key">Radarr's API key.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The movies by TMDB id.</returns>
    public async Task<Dictionary<int, (int Id, bool HasFile)>> ListMoviesAsync(string url, string key, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, (int, bool)>();
        var items = (await GetAsync(url, key, "/api/v3/movie", cancellationToken).ConfigureAwait(false))?.AsArray();
        if (items is null)
        {
            return result;
        }

        foreach (var item in items)
        {
            var tmdb = item?["tmdbId"]?.GetValue<int>() ?? 0;
            if (tmdb > 0)
            {
                result[tmdb] = (item?["id"]?.GetValue<int>() ?? 0, item?["hasFile"]?.GetValue<bool>() ?? false);
            }
        }

        return result;
    }

    /// <summary>
    /// Lists what Sonarr holds: TMDB id to Sonarr id and how many episode files are on disk.
    /// </summary>
    /// <param name="url">Sonarr's address.</param>
    /// <param name="key">Sonarr's API key.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The series by TMDB id (series without one are keyed by negative TVDB id).</returns>
    public async Task<Dictionary<int, (int Id, int Files)>> ListSeriesAsync(string url, string key, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, (int, int)>();
        var items = (await GetAsync(url, key, "/api/v3/series", cancellationToken).ConfigureAwait(false))?.AsArray();
        if (items is null)
        {
            return result;
        }

        foreach (var item in items)
        {
            var tmdb = item?["tmdbId"]?.GetValue<int>() ?? 0;
            var tvdb = item?["tvdbId"]?.GetValue<int>() ?? 0;
            var files = item?["statistics"]?["episodeFileCount"]?.GetValue<int>() ?? 0;
            var id = item?["id"]?.GetValue<int>() ?? 0;
            if (tmdb > 0)
            {
                result[tmdb] = (id, files);
            }
            else if (tvdb > 0)
            {
                result[-tvdb] = (id, files);
            }
        }

        return result;
    }

    /// <summary>
    /// Finds the movie in Radarr by TMDB id, adding it unmonitored (and without searching) when it is not there.
    /// </summary>
    /// <param name="settings">The Discover settings.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Radarr's id for the movie, 0 when Radarr does not know it.</returns>
    public async Task<int> EnsureMovieAsync(DiscoverSettings settings, int tmdbId, CancellationToken cancellationToken)
    {
        var known = await ListMoviesAsync(settings.RadarrUrl, settings.RadarrApiKey, cancellationToken).ConfigureAwait(false);
        if (known.TryGetValue(tmdbId, out var existing))
        {
            return existing.Id;
        }

        var lookup = await GetAsync(settings.RadarrUrl, settings.RadarrApiKey, "/api/v3/movie/lookup/tmdb?tmdbId=" + tmdbId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        if (lookup is not JsonObject movie)
        {
            return 0;
        }

        movie["qualityProfileId"] = await FirstIdAsync(settings.RadarrUrl, settings.RadarrApiKey, "/api/v3/qualityprofile", cancellationToken).ConfigureAwait(false);
        movie["rootFolderPath"] = await RootFolderAsync(settings.RadarrUrl, settings.RadarrApiKey, settings.RadarrRootFolder, cancellationToken).ConfigureAwait(false);
        movie["monitored"] = false;
        movie["minimumAvailability"] = "released";
        movie["addOptions"] = new JsonObject { ["searchForMovie"] = false, ["monitor"] = "none" };
        var added = await PostAsync(settings.RadarrUrl, settings.RadarrApiKey, "/api/v3/movie", movie, cancellationToken).ConfigureAwait(false);
        return added?["id"]?.GetValue<int>() ?? 0;
    }

    /// <summary>
    /// Finds the series in Sonarr, adding it unmonitored (and without searching) when it is not there.
    /// </summary>
    /// <param name="settings">The Discover settings.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="tvdbId">The TVDB id Sonarr's lookup works from.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Sonarr's id for the series, 0 when Sonarr does not know it.</returns>
    public async Task<int> EnsureSeriesAsync(DiscoverSettings settings, int tmdbId, int tvdbId, CancellationToken cancellationToken)
    {
        var known = await ListSeriesAsync(settings.SonarrUrl, settings.SonarrApiKey, cancellationToken).ConfigureAwait(false);
        if (known.TryGetValue(tmdbId, out var existing) || (tvdbId > 0 && known.TryGetValue(-tvdbId, out existing)))
        {
            return existing.Id;
        }

        if (tvdbId <= 0)
        {
            return 0;
        }

        var lookup = await GetAsync(settings.SonarrUrl, settings.SonarrApiKey, "/api/v3/series/lookup?term=tvdb:" + tvdbId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        if (lookup?.AsArray() is not { Count: > 0 } found || found[0] is not JsonObject series)
        {
            return 0;
        }

        series["qualityProfileId"] = await FirstIdAsync(settings.SonarrUrl, settings.SonarrApiKey, "/api/v3/qualityprofile", cancellationToken).ConfigureAwait(false);
        series["rootFolderPath"] = await RootFolderAsync(settings.SonarrUrl, settings.SonarrApiKey, settings.SonarrRootFolder, cancellationToken).ConfigureAwait(false);
        series["monitored"] = false;
        series["seasonFolder"] = true;
        series["addOptions"] = new JsonObject { ["monitor"] = "none", ["searchForMissingEpisodes"] = false, ["searchForCutoffUnmetEpisodes"] = false };
        var added = await PostAsync(settings.SonarrUrl, settings.SonarrApiKey, "/api/v3/series", series, cancellationToken).ConfigureAwait(false);
        return added?["id"]?.GetValue<int>() ?? 0;
    }

    /// <summary>
    /// Finds Sonarr's id for one episode.
    /// </summary>
    /// <param name="settings">The Discover settings.</param>
    /// <param name="seriesId">Sonarr's series id.</param>
    /// <param name="season">The season.</param>
    /// <param name="episode">The episode.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The episode id, 0 when Sonarr has no such episode.</returns>
    public async Task<int> FindEpisodeAsync(DiscoverSettings settings, int seriesId, int season, int episode, CancellationToken cancellationToken)
    {
        var items = (await GetAsync(settings.SonarrUrl, settings.SonarrApiKey, "/api/v3/episode?seriesId=" + seriesId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false))?.AsArray();
        if (items is null)
        {
            return 0;
        }

        foreach (var item in items)
        {
            if ((item?["seasonNumber"]?.GetValue<int>() ?? -1) == season && (item?["episodeNumber"]?.GetValue<int>() ?? -1) == episode)
            {
                return item?["id"]?.GetValue<int>() ?? 0;
            }
        }

        return 0;
    }

    /// <summary>
    /// Asks Radarr to search its indexers for a movie and returns what they offer.
    /// </summary>
    /// <param name="settings">The Discover settings.</param>
    /// <param name="radarrMovieId">Radarr's movie id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The releases, unfiltered.</returns>
    public Task<List<ReleaseOption>> GetMovieReleasesAsync(DiscoverSettings settings, int radarrMovieId, CancellationToken cancellationToken)
    {
        return GetReleasesAsync(settings.RadarrUrl, settings.RadarrApiKey, "/api/v3/release?movieId=" + radarrMovieId.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    /// <summary>
    /// Asks Sonarr to search its indexers for one episode and returns what they offer, season packs included.
    /// </summary>
    /// <param name="settings">The Discover settings.</param>
    /// <param name="episodeId">Sonarr's episode id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The releases, unfiltered.</returns>
    public Task<List<ReleaseOption>> GetEpisodeReleasesAsync(DiscoverSettings settings, int episodeId, CancellationToken cancellationToken)
    {
        return GetReleasesAsync(settings.SonarrUrl, settings.SonarrApiKey, "/api/v3/release?episodeId=" + episodeId.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    /// <summary>
    /// Tells Radarr or Sonarr to download a release it found, so the title is kept and imported.
    /// </summary>
    /// <param name="url">The address of Radarr or Sonarr.</param>
    /// <param name="key">The API key.</param>
    /// <param name="release">The release to grab.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it accepted the grab.</returns>
    public async Task<bool> GrabAsync(string url, string key, ReleaseOption release, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["guid"] = release.Guid, ["indexerId"] = release.IndexerId };
        var response = await PostAsync(url, key, "/api/v3/release", body, cancellationToken).ConfigureAwait(false);
        return response is not null;
    }

    /// <summary>
    /// Makes a short id for a release from its indexer and guid, so a picked release can be found again.
    /// </summary>
    /// <param name="indexerId">The indexer id.</param>
    /// <param name="guid">The release guid.</param>
    /// <returns>Eight lowercase hex characters.</returns>
    public static string MakeReleaseId(int indexerId, string guid)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(indexerId.ToString(CultureInfo.InvariantCulture) + "|" + guid));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private async Task<List<ReleaseOption>> GetReleasesAsync(string url, string key, string path, CancellationToken cancellationToken)
    {
        var releases = new List<ReleaseOption>();
        var items = (await GetAsync(url, key, path, cancellationToken).ConfigureAwait(false))?.AsArray();
        if (items is null)
        {
            return releases;
        }

        foreach (var item in items)
        {
            if (item is null || !string.Equals(item["protocol"]?.GetValue<string>(), "torrent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var guid = item["guid"]?.GetValue<string>() ?? string.Empty;
            var indexerId = item["indexerId"]?.GetValue<int>() ?? 0;
            var release = new ReleaseOption
            {
                Id = MakeReleaseId(indexerId, guid),
                Title = item["title"]?.GetValue<string>() ?? string.Empty,
                Guid = guid,
                IndexerId = indexerId,
                Indexer = item["indexer"]?.GetValue<string>() ?? string.Empty,
                SizeBytes = item["size"]?.GetValue<long>() ?? 0,
                Seeders = item["seeders"]?.GetValue<int>() ?? 0,
                MagnetUrl = item["magnetUrl"]?.GetValue<string>() ?? string.Empty,
                DownloadUrl = item["downloadUrl"]?.GetValue<string>() ?? string.Empty,
                InfoHash = item["infoHash"]?.GetValue<string>() ?? string.Empty,
                IsSeasonPack = item["fullSeason"]?.GetValue<bool>() ?? false,
                Rejected = item["rejected"]?.GetValue<bool>() ?? false,
                Resolution = item["quality"]?["quality"]?["resolution"]?.GetValue<int>() ?? 0,
            };
            var quality = item["quality"]?["quality"]?["name"]?.GetValue<string>() ?? string.Empty;
            release.Quality = quality;
            if (item["languages"]?.AsArray() is { } languages)
            {
                foreach (var language in languages)
                {
                    var name = language?["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(name))
                    {
                        release.Languages.Add(name);
                    }
                }
            }

            if (!string.IsNullOrEmpty(release.TorrentLink))
            {
                releases.Add(release);
            }
        }

        return releases;
    }

    private async Task<JsonNode?> GetAsync(string url, string key, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url + path);
        request.Headers.Add("X-Api-Key", key);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private async Task<JsonNode?> PostAsync(string url, string key, string path, JsonNode body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url + path);
        request.Headers.Add("X-Api-Key", key);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text);
    }

    private async Task<int> FirstIdAsync(string url, string key, string path, CancellationToken cancellationToken)
    {
        var items = (await GetAsync(url, key, path, cancellationToken).ConfigureAwait(false))?.AsArray();
        return items is { Count: > 0 } ? items[0]?["id"]?.GetValue<int>() ?? 1 : 1;
    }

    private async Task<string> RootFolderAsync(string url, string key, string configured, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var items = (await GetAsync(url, key, "/api/v3/rootfolder", cancellationToken).ConfigureAwait(false))?.AsArray();
        return items is { Count: > 0 } ? items[0]?["path"]?.GetValue<string>() ?? string.Empty : string.Empty;
    }
}
