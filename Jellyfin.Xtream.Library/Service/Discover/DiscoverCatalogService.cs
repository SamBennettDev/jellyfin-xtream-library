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
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service.Discover;

/// <summary>
/// What a catalog sync did.
/// </summary>
/// <param name="MoviesWritten">Movie placeholders written or kept.</param>
/// <param name="SeriesWritten">Series with placeholders written or kept.</param>
/// <param name="EpisodesWritten">Episode placeholder files created.</param>
/// <param name="Removed">Folders removed (title now owned, or dropped from the catalog).</param>
/// <param name="SkippedOwned">Titles left out because Radarr or Sonarr already has them.</param>
public sealed record DiscoverSyncResult(int MoviesWritten, int SeriesWritten, int EpisodesWritten, int Removed, int SkippedOwned);

/// <summary>
/// Builds the Discover libraries: a placeholder (a STRM file pointing back at this plugin) for every popular English
/// title that Radarr and Sonarr do not already hold, so Jellyfin shows its TMDB posters and metadata. Titles you
/// own stay in your real libraries and are left out here.
/// </summary>
public sealed partial class DiscoverCatalogService : IDisposable
{
    /// <summary>
    /// The name of the movie library Discover creates.
    /// </summary>
    public const string MoviesLibraryName = "Discover Movies";

    /// <summary>
    /// The name of the series library Discover creates.
    /// </summary>
    public const string ShowsLibraryName = "Discover Shows";

    private static readonly TimeSpan SeriesRefreshAge = TimeSpan.FromDays(7);

    private readonly TmdbClient _tmdb;
    private readonly ArrClient _arr;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<DiscoverCatalogService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoverCatalogService"/> class.
    /// </summary>
    /// <param name="tmdb">The TMDB client.</param>
    /// <param name="arr">The Radarr and Sonarr client.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="appHost">The server host (for its own address).</param>
    /// <param name="logger">The logger.</param>
    public DiscoverCatalogService(TmdbClient tmdb, ArrClient arr, ILibraryManager libraryManager, IServerApplicationHost appHost, ILogger<DiscoverCatalogService> logger)
    {
        _tmdb = tmdb;
        _arr = arr;
        _libraryManager = libraryManager;
        _appHost = appHost;
        _logger = logger;
    }

    /// <summary>
    /// Makes a string safe to use as a file or folder name.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The name without characters filesystems reject, trimmed and length-limited.</returns>
    public static string SafeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c is ':' or '"' or '?' or '*' ? ' ' : c);
        }

        var cleaned = Regex.Replace(builder.ToString(), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Trim().TrimEnd('.');
        return cleaned.Length > 120 ? cleaned[..120].TrimEnd() : cleaned;
    }

    /// <summary>
    /// Builds the folder name for a title: "Name (Year) [tmdbid-123]".
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>The folder name.</returns>
    public static string FolderName(CatalogTitle title)
    {
        var name = SafeName(title.Title);
        return title.Year > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{name} ({title.Year}) [tmdbid-{title.TmdbId}]")
            : string.Create(CultureInfo.InvariantCulture, $"{name} [tmdbid-{title.TmdbId}]");
    }

    /// <summary>
    /// Brings the placeholder folders up to date with TMDB, Radarr and Sonarr, and makes sure the libraries exist.
    /// </summary>
    /// <param name="progress">Progress, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What it did.</returns>
    public async Task<DiscoverSyncResult> SyncAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var settings = Plugin.Instance.Configuration.Discover;
        if (!settings.Enabled)
        {
            return new DiscoverSyncResult(0, 0, 0, 0, 0);
        }

        if (string.IsNullOrWhiteSpace(settings.TmdbApiKey))
        {
            throw new InvalidOperationException("Discover needs a TMDB API key; set one in the Discover settings.");
        }

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SyncCoreAsync(settings, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _runLock.Dispose();
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return;
        }

        File.WriteAllText(path, content);
    }

    private async Task<DiscoverSyncResult> SyncCoreAsync(DiscoverSettings settings, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var baseUrl = ResolveBaseUrl();
        var moviesRoot = Path.Combine(settings.CatalogPath, "Movies");
        var showsRoot = Path.Combine(settings.CatalogPath, "Shows");
        Directory.CreateDirectory(moviesRoot);
        Directory.CreateDirectory(showsRoot);
        await EnsureLibrariesAsync(settings, moviesRoot, showsRoot).ConfigureAwait(false);
        progress.Report(2);

        // What Radarr and Sonarr already hold on disk. A title there lives in your real libraries.
        var ownedMovies = new HashSet<int>();
        if (!string.IsNullOrEmpty(settings.RadarrUrl) && !string.IsNullOrEmpty(settings.RadarrApiKey))
        {
            foreach (var (tmdb, info) in await _arr.ListMoviesAsync(settings.RadarrUrl, settings.RadarrApiKey, cancellationToken).ConfigureAwait(false))
            {
                if (info.HasFile)
                {
                    ownedMovies.Add(tmdb);
                }
            }
        }

        var ownedSeries = new HashSet<int>();
        if (!string.IsNullOrEmpty(settings.SonarrUrl) && !string.IsNullOrEmpty(settings.SonarrApiKey))
        {
            foreach (var (tmdb, info) in await _arr.ListSeriesAsync(settings.SonarrUrl, settings.SonarrApiKey, cancellationToken).ConfigureAwait(false))
            {
                if (info.Files > 0 && tmdb > 0)
                {
                    ownedSeries.Add(tmdb);
                }
            }
        }

        progress.Report(5);
        var skipped = 0;
        var removed = 0;
        var wantedMovies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wantedShows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Movies.
        var movies = settings.MaxMovies > 0
            ? await _tmdb.ListPopularAsync(settings, movies: true, settings.MaxMovies, cancellationToken).ConfigureAwait(false)
            : new List<CatalogTitle>();
        foreach (var movie in movies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ownedMovies.Contains(movie.TmdbId))
            {
                skipped++;
                continue;
            }

            var folder = FolderName(movie);
            wantedMovies.Add(folder);
            var dir = Path.Combine(moviesRoot, folder);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, SafeName(movie.Year > 0 ? $"{movie.Title} ({movie.Year})" : movie.Title) + ".strm");
            WriteIfChanged(file, baseUrl + new TitleRef(true, movie.TmdbId, 0, 0).ToPath());
        }

        progress.Report(30);

        // Series: placeholders per aired episode, refetched at most weekly.
        var series = settings.MaxSeries > 0
            ? await _tmdb.ListPopularAsync(settings, movies: false, settings.MaxSeries, cancellationToken).ConfigureAwait(false)
            : new List<CatalogTitle>();
        var episodesWritten = 0;
        var seriesWritten = 0;
        var done = 0;
        using var gate = new SemaphoreSlim(4);
        var tasks = series.Select(async show =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (ownedSeries.Contains(show.TmdbId))
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                var folder = FolderName(show);
                lock (wantedShows)
                {
                    wantedShows.Add(folder);
                }

                var dir = Path.Combine(showsRoot, folder);
                var fresh = Directory.Exists(dir)
                    && Directory.EnumerateFiles(dir, "*.strm", SearchOption.AllDirectories).Any()
                    && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < SeriesRefreshAge;
                if (fresh)
                {
                    Interlocked.Increment(ref seriesWritten);
                    return;
                }

                var episodes = await _tmdb.GetEpisodesAsync(settings, show.TmdbId, cancellationToken).ConfigureAwait(false);
                if (episodes.Count == 0)
                {
                    return;
                }

                Directory.CreateDirectory(dir);
                var created = 0;
                foreach (var episode in episodes)
                {
                    var seasonDir = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"Season {episode.Season:00}"));
                    Directory.CreateDirectory(seasonDir);
                    var name = SafeName(show.Title) + string.Create(CultureInfo.InvariantCulture, $" - S{episode.Season:00}E{episode.Episode:00}");
                    var path = Path.Combine(seasonDir, name + ".strm");
                    if (!File.Exists(path))
                    {
                        created++;
                    }

                    WriteIfChanged(path, baseUrl + new TitleRef(false, show.TmdbId, episode.Season, episode.Episode).ToPath());
                }

                Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
                Interlocked.Add(ref episodesWritten, created);
                Interlocked.Increment(ref seriesWritten);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Discover: could not build series {Title} ({Id})", show.Title, show.TmdbId);
            }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref done);
                progress.Report(30 + (60d * n / Math.Max(1, series.Count)));
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Remove what is no longer wanted: titles now owned, or fallen out of the catalog.
        removed += Prune(moviesRoot, wantedMovies);
        removed += Prune(showsRoot, wantedShows);
        progress.Report(95);

        if (removed > 0 || episodesWritten > 0 || movies.Count > 0)
        {
            _libraryManager.QueueLibraryScan();
        }

        progress.Report(100);
        var result = new DiscoverSyncResult(wantedMovies.Count, seriesWritten, episodesWritten, removed, skipped);
        _logger.LogInformation(
            "Discover catalog: {Movies} movies, {Series} series, {Episodes} new episodes, {Removed} removed, {Skipped} already owned",
            result.MoviesWritten,
            result.SeriesWritten,
            result.EpisodesWritten,
            result.Removed,
            result.SkippedOwned);
        return result;
    }

    private static int Prune(string root, HashSet<string> wanted)
    {
        var removed = 0;
        var rootFull = Path.GetFullPath(root);
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (wanted.Contains(name) || !TmdbFolderRegex().IsMatch(name))
            {
                continue;
            }

            // Never delete outside the catalog, whatever the listing returned.
            var full = Path.GetFullPath(dir);
            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            Directory.Delete(full, recursive: true);
            removed++;
        }

        return removed;
    }

    [GeneratedRegex(@"\[tmdbid-\d+\]$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TmdbFolderRegex();

    private string ResolveBaseUrl()
    {
        try
        {
            var url = _appHost.GetApiUrlForLocalAccess(IPAddress.Loopback, false);
            return string.IsNullOrEmpty(url) ? "http://127.0.0.1:8096" : url.TrimEnd('/');
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the local server address; using 127.0.0.1:8096");
            return "http://127.0.0.1:8096";
        }
    }

    private async Task EnsureLibrariesAsync(DiscoverSettings settings, string moviesRoot, string showsRoot)
    {
        var existing = _libraryManager.GetVirtualFolders().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(MoviesLibraryName))
        {
            await _libraryManager.AddVirtualFolder(MoviesLibraryName, CollectionTypeOptions.movies, NewLibraryOptions(moviesRoot), refreshLibrary: false).ConfigureAwait(false);
            _logger.LogInformation("Discover: created the {Name} library at {Path}", MoviesLibraryName, moviesRoot);
        }

        if (!existing.Contains(ShowsLibraryName))
        {
            await _libraryManager.AddVirtualFolder(ShowsLibraryName, CollectionTypeOptions.tvshows, NewLibraryOptions(showsRoot), refreshLibrary: false).ConfigureAwait(false);
            _logger.LogInformation("Discover: created the {Name} library at {Path}", ShowsLibraryName, showsRoot);
        }
    }

    private static LibraryOptions NewLibraryOptions(string path)
    {
        return new LibraryOptions
        {
            PathInfos = new[] { new MediaPathInfo(path) },
            EnableRealtimeMonitor = false,
            SaveLocalMetadata = false,
        };
    }
}
