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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service.Discover;

/// <summary>
/// A release opened in TorrServer and ready to play.
/// </summary>
/// <param name="Url">The stream address.</param>
/// <param name="FileName">The video file's name.</param>
/// <param name="SizeBytes">The video file's size.</param>
/// <param name="Hash">The torrent's info hash.</param>
/// <param name="Release">The release it came from.</param>
public sealed record OpenedRelease(string Url, string FileName, long SizeBytes, string Hash, ReleaseOption Release);

/// <summary>
/// Finds releases for a title in Radarr or Sonarr and opens one in TorrServer. This is the shared core of the version
/// picker (which lists releases), the automatic default (which opens the best one) and the stream itself.
/// </summary>
public sealed class DiscoverResolver
{
    private static readonly TimeSpan ReleaseCacheAge = TimeSpan.FromMinutes(30);

    private readonly ArrClient _arr;
    private readonly TmdbClient _tmdb;
    private readonly TorrServerClient _torrServer;
    private readonly ILogger<DiscoverResolver> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, List<ReleaseOption> Releases)> _releaseCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, OpenedRelease> _opened = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoverResolver"/> class.
    /// </summary>
    /// <param name="arr">The Radarr and Sonarr client.</param>
    /// <param name="tmdb">The TMDB client.</param>
    /// <param name="torrServer">The TorrServer client.</param>
    /// <param name="logger">The logger.</param>
    public DiscoverResolver(ArrClient arr, TmdbClient tmdb, TorrServerClient torrServer, ILogger<DiscoverResolver> logger)
    {
        _arr = arr;
        _tmdb = tmdb;
        _torrServer = torrServer;
        _logger = logger;
    }

    /// <summary>
    /// Lists the releases on offer for a title, best first. Results are kept for half an hour, because a search
    /// takes seconds and a client asks for the version list more than once.
    /// </summary>
    /// <param name="title">The movie or episode.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The releases to offer, possibly empty.</returns>
    public async Task<IReadOnlyList<ReleaseOption>> GetReleasesAsync(TitleRef title, CancellationToken cancellationToken)
    {
        var settings = Plugin.Instance.Configuration.Discover;
        var key = Key(title);
        if (_releaseCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < ReleaseCacheAge)
        {
            return cached.Releases;
        }

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_releaseCache.TryGetValue(key, out cached) && DateTime.UtcNow - cached.At < ReleaseCacheAge)
            {
                return cached.Releases;
            }

            var found = new List<ReleaseOption>();
            if (title.IsMovie)
            {
                if (!string.IsNullOrEmpty(settings.RadarrUrl))
                {
                    var movieId = await _arr.EnsureMovieAsync(settings, title.TmdbId, cancellationToken).ConfigureAwait(false);
                    if (movieId > 0)
                    {
                        found = await _arr.GetMovieReleasesAsync(settings, movieId, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else if (!string.IsNullOrEmpty(settings.SonarrUrl))
            {
                var tvdb = await _tmdb.GetTvdbIdAsync(settings, title.TmdbId, cancellationToken).ConfigureAwait(false);
                var seriesId = await _arr.EnsureSeriesAsync(settings, title.TmdbId, tvdb, cancellationToken).ConfigureAwait(false);
                if (seriesId > 0)
                {
                    var episodeId = await _arr.FindEpisodeAsync(settings, seriesId, title.Season, title.Episode, cancellationToken).ConfigureAwait(false);
                    if (episodeId > 0)
                    {
                        found = await _arr.GetEpisodeReleasesAsync(settings, episodeId, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            var chosen = ReleaseLabeler.Select(found, settings);
            _logger.LogInformation("Discover: {Found} releases found for {Key}, offering {Offered}", found.Count, key, chosen.Count);
            _releaseCache[key] = (DateTime.UtcNow, chosen);
            return chosen;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Opens a release in TorrServer, picking the right file, and returns where to stream it from.
    /// </summary>
    /// <param name="title">The movie or episode.</param>
    /// <param name="releaseId">The release to open, or null or empty for the best one.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The opened release, or null when nothing could be opened.</returns>
    public async Task<OpenedRelease?> OpenAsync(TitleRef title, string? releaseId, CancellationToken cancellationToken)
    {
        var settings = Plugin.Instance.Configuration.Discover;
        var releases = await GetReleasesAsync(title, cancellationToken).ConfigureAwait(false);
        var candidates = string.IsNullOrEmpty(releaseId)
            ? releases.ToList()
            : releases.Where(r => r.Id == releaseId).Concat(releases.Where(r => r.Id != releaseId)).ToList();

        // A chosen release is tried first; if it will not load, fall back down the list rather than fail the play.
        var tried = 0;
        foreach (var release in candidates)
        {
            if (tried++ >= 4)
            {
                break;
            }

            var cacheKey = Key(title) + "|" + release.Id;
            if (_opened.TryGetValue(cacheKey, out var known))
            {
                return known;
            }

            var loaded = await _torrServer.AddAsync(
                settings.TorrServerUrl,
                release.TorrentLink,
                release.Title,
                TimeSpan.FromSeconds(settings.TorrentTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                _logger.LogWarning("Discover: torrent for {Release} did not load in time; trying the next release", release.Title);
                continue;
            }

            var index = ReleaseLabeler.PickFile(loaded.Files, title.IsMovie ? 0 : title.Season, title.IsMovie ? 0 : title.Episode);
            if (index < 0)
            {
                _logger.LogWarning("Discover: no matching video file in {Release}; trying the next release", release.Title);
                continue;
            }

            var file = loaded.Files.First(f => f.Index == index);
            var opened = new OpenedRelease(TorrServerClient.StreamUrl(settings.TorrServerUrl, loaded.Hash, index, file.Path), file.Path, file.Size, loaded.Hash, release);
            _opened[cacheKey] = opened;
            _logger.LogInformation("Discover: opened {File} from {Release}", file.Path, release.Title);

            if (settings.KeepAfterStreaming)
            {
                await KeepAsync(title, release, cancellationToken).ConfigureAwait(false);
            }

            return opened;
        }

        return null;
    }

    private static string Key(TitleRef title) =>
        title.IsMovie ? $"movie:{title.TmdbId}" : $"episode:{title.TmdbId}:{title.Season}:{title.Episode}";

    private async Task KeepAsync(TitleRef title, ReleaseOption release, CancellationToken cancellationToken)
    {
        var settings = Plugin.Instance.Configuration.Discover;
        try
        {
            var ok = title.IsMovie
                ? await _arr.GrabAsync(settings.RadarrUrl, settings.RadarrApiKey, release, cancellationToken).ConfigureAwait(false)
                : await _arr.GrabAsync(settings.SonarrUrl, settings.SonarrApiKey, release, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Discover: sent {Release} to {App} to keep: {Ok}", release.Title, title.IsMovie ? "Radarr" : "Sonarr", ok);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            // The stream already works; failing to keep it must not break playback.
            _logger.LogWarning(ex, "Discover: could not send {Release} to be kept", release.Title);
        }
    }
}
