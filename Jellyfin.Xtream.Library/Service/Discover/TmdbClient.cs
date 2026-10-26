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
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Library.Service.Discover;

/// <summary>
/// The few TMDB v3 calls Discover needs: listing popular titles and reading a series' seasons and episodes.
/// </summary>
public sealed class TmdbClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const int MaxPage = 500;
    private const int MaxPerQuery = 10000;

    private readonly HttpClient _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbClient"/> class.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    public TmdbClient(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Lists the most popular titles. TMDB returns at most 10,000 results for one query, so a longer list is built
    /// from one query per release year and merged by popularity.
    /// </summary>
    /// <param name="settings">The Discover settings (key, language).</param>
    /// <param name="movies">Whether to list movies (otherwise series).</param>
    /// <param name="limit">The most titles to return.</param>
    /// <param name="minVotes">The fewest votes a title needs.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The titles, most popular first.</returns>
    public async Task<List<CatalogTitle>> ListPopularAsync(DiscoverSettings settings, bool movies, int limit, int minVotes, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return new List<CatalogTitle>();
        }

        if (limit <= MaxPerQuery)
        {
            return await ListPagesAsync(settings, movies, "popularity.desc", minVotes, null, null, limit, cancellationToken).ConfigureAwait(false);
        }

        var found = new Dictionary<int, CatalogTitle>();
        var lockObject = new object();
        using var gate = new SemaphoreSlim(6);
        var thisYear = DateTime.UtcNow.Year;
        var tasks = Enumerable.Range(1900, thisYear - 1900 + 1).Select(async year =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var part = await ListPagesAsync(
                    settings,
                    movies,
                    "popularity.desc",
                    minVotes,
                    string.Create(CultureInfo.InvariantCulture, $"{year}-01-01"),
                    string.Create(CultureInfo.InvariantCulture, $"{year}-12-31"),
                    MaxPerQuery,
                    cancellationToken).ConfigureAwait(false);
                lock (lockObject)
                {
                    foreach (var title in part)
                    {
                        found[title.TmdbId] = title;
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return found.Values.OrderByDescending(t => t.Popularity).Take(limit).ToList();
    }

    /// <summary>
    /// Lists the newest titles that have already been released or aired, newest first.
    /// </summary>
    /// <param name="settings">The Discover settings.</param>
    /// <param name="movies">Whether to list movies (otherwise series).</param>
    /// <param name="limit">The most titles to return (up to 10,000, TMDB's cap for one query).</param>
    /// <param name="minVotes">The fewest votes a title needs.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The titles, newest first.</returns>
    public Task<List<CatalogTitle>> ListNewestAsync(DiscoverSettings settings, bool movies, int limit, int minVotes, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return Task.FromResult(new List<CatalogTitle>());
        }

        var sort = movies ? "primary_release_date.desc" : "first_air_date.desc";
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return ListPagesAsync(settings, movies, sort, minVotes, null, today, Math.Min(limit, MaxPerQuery), cancellationToken);
    }

    private async Task<List<CatalogTitle>> ListPagesAsync(
        DiscoverSettings settings,
        bool movies,
        string sort,
        int minVotes,
        string? from,
        string? to,
        int limit,
        CancellationToken cancellationToken)
    {
        var result = new List<CatalogTitle>();
        var kind = movies ? "movie" : "tv";
        var dateField = movies ? "primary_release_date" : "first_air_date";
        for (var page = 1; page <= MaxPage && result.Count < limit; page++)
        {
            var url = $"{BaseUrl}/discover/{kind}?with_original_language={Uri.EscapeDataString(settings.Language)}"
                + $"&vote_count.gte={minVotes.ToString(CultureInfo.InvariantCulture)}&sort_by={sort}"
                + $"&include_adult=false&page={page.ToString(CultureInfo.InvariantCulture)}";
            if (from is not null)
            {
                url += $"&{dateField}.gte={from}";
            }

            if (to is not null)
            {
                url += $"&{dateField}.lte={to}";
            }

            var json = await GetAsync(settings, url, cancellationToken).ConfigureAwait(false);
            var items = json?["results"]?.AsArray();
            if (items is null || items.Count == 0)
            {
                break;
            }

            foreach (var item in items)
            {
                var id = item?["id"]?.GetValue<int>() ?? 0;
                var title = (movies ? item?["title"] : item?["name"])?.GetValue<string>();
                if (id <= 0 || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var date = (movies ? item?["release_date"] : item?["first_air_date"])?.GetValue<string>();
                result.Add(new CatalogTitle(
                    id,
                    movies,
                    title,
                    ParseYear(date),
                    item?["overview"]?.GetValue<string>() ?? string.Empty,
                    item?["popularity"]?.GetValue<double>() ?? 0));
                if (result.Count >= limit)
                {
                    break;
                }
            }

            var totalPages = json?["total_pages"]?.GetValue<int>() ?? 0;
            if (page >= totalPages)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Reads every aired episode of a series, season 0 (specials) left out.
    /// </summary>
    /// <param name="settings">The Discover settings.</param>
    /// <param name="seriesId">The TMDB series id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The episodes in order.</returns>
    public async Task<List<CatalogEpisode>> GetEpisodesAsync(DiscoverSettings settings, int seriesId, CancellationToken cancellationToken)
    {
        var episodes = new List<CatalogEpisode>();
        var details = await GetAsync(settings, $"{BaseUrl}/tv/{seriesId.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
        var seasons = details?["seasons"]?.AsArray();
        if (seasons is null)
        {
            return episodes;
        }

        var today = DateTime.UtcNow.Date;
        foreach (var season in seasons)
        {
            var number = season?["season_number"]?.GetValue<int>() ?? 0;
            if (number <= 0)
            {
                continue;
            }

            var json = await GetAsync(settings, $"{BaseUrl}/tv/{seriesId.ToString(CultureInfo.InvariantCulture)}/season/{number.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
            var list = json?["episodes"]?.AsArray();
            if (list is null)
            {
                continue;
            }

            foreach (var item in list)
            {
                var episode = item?["episode_number"]?.GetValue<int>() ?? 0;
                if (episode <= 0)
                {
                    continue;
                }

                var aired = item?["air_date"]?.GetValue<string>();
                if (string.IsNullOrEmpty(aired)
                    || !DateTime.TryParse(aired, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var airDate)
                    || airDate.Date > today)
                {
                    continue;
                }

                episodes.Add(new CatalogEpisode(
                    number,
                    episode,
                    item?["name"]?.GetValue<string>() ?? $"Episode {episode}",
                    item?["overview"]?.GetValue<string>() ?? string.Empty,
                    item?["runtime"]?.GetValue<int>() ?? 0));
            }
        }

        return episodes;
    }

    /// <summary>
    /// Reads the TVDB id of a series, which Sonarr identifies it by.
    /// </summary>
    /// <param name="settings">The Discover settings.</param>
    /// <param name="seriesId">The TMDB series id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The TVDB id, or 0 when TMDB has none.</returns>
    public async Task<int> GetTvdbIdAsync(DiscoverSettings settings, int seriesId, CancellationToken cancellationToken)
    {
        var json = await GetAsync(settings, $"{BaseUrl}/tv/{seriesId.ToString(CultureInfo.InvariantCulture)}/external_ids", cancellationToken).ConfigureAwait(false);
        return json?["tvdb_id"]?.GetValue<int>() ?? 0;
    }

    /// <summary>
    /// Checks the key by asking TMDB for its configuration.
    /// </summary>
    /// <param name="settings">The Discover settings.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether TMDB accepted the key.</returns>
    public async Task<bool> TestAsync(DiscoverSettings settings, CancellationToken cancellationToken)
    {
        var json = await GetAsync(settings, $"{BaseUrl}/configuration", cancellationToken).ConfigureAwait(false);
        return json is not null;
    }

    private static int ParseYear(string? date)
    {
        return date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : 0;
    }

    private async Task<JsonNode?> GetAsync(DiscoverSettings settings, string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.TmdbApiKey))
        {
            throw new InvalidOperationException("No TMDB API key is set in the Discover settings.");
        }

        // A v4 read token is a JWT and goes in a header; a v3 key goes in the query.
        var bearer = settings.TmdbApiKey.StartsWith("eyJ", StringComparison.Ordinal);
        var address = bearer ? url : url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api_key=" + Uri.EscapeDataString(settings.TmdbApiKey);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (bearer)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.TmdbApiKey);
            }

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode == 429 && attempt < 3)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1));
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonNode.Parse(text);
        }

        return null;
    }
}
